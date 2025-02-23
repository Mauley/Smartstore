﻿using Microsoft.AspNetCore.Diagnostics;
using Microsoft.AspNetCore.Http;
using Smartstore.Caching;
using Smartstore.Core.Checkout.Tax;
using Smartstore.Core.Common;
using Smartstore.Core.Common.Services;
using Smartstore.Core.Configuration;
using Smartstore.Core.Data;
using Smartstore.Core.Identity;
using Smartstore.Core.Localization;
using Smartstore.Core.Security;
using Smartstore.Core.Stores;
using Smartstore.Core.Web;
using Smartstore.Data.Hooks;
using Smartstore.Net;
using Smartstore.Threading;
using Smartstore.Utilities;

namespace Smartstore.Core
{
    public class DefaultWorkContextSource : AsyncDbSaveHook<BaseEntity>, IWorkContextSource
    {
        /// <summary>
        /// Key for tax display type caching
        /// </summary>
        /// <remarks>
        /// {0} : customer role ids
        /// {1} : store identifier
        /// </remarks>
        const string CUSTOMERROLES_TAX_DISPLAY_TYPES_KEY = "customerroles:taxdisplaytypes-{0}-{1}";
        const string CUSTOMERROLES_TAX_DISPLAY_TYPES_PATTERN_KEY = "customerroles:taxdisplaytypes*";

        private readonly static Func<DetectCustomerContext, Task<Customer>>[] _customerDetectors =
        [
            DetectTaskScheduler,
            DetectPdfConverter,
            DetectAuthenticated,
            DetectGuest,
            // INFO: We rely on overload protector's ForbidNewGuestsIfSubRequest policy for now.
            //DetectBotForMedia,
            DetectBot,
            DetectWebhookEndpoint,
            DetectByClientIdent
        ];

        private readonly SmartDbContext _db;
        private readonly IHttpContextAccessor _httpContextAccessor;
        private readonly ILanguageResolver _languageResolver;
        private readonly IStoreContext _storeContext;
        private readonly ICustomerService _customerService;
        private readonly Lazy<ITaxService> _taxService;
        private readonly Lazy<ICurrencyService> _currencyService;
        private readonly TaxSettings _taxSettings;
        private readonly ICacheManager _cache;
        private readonly IUserAgent _userAgent;
        private readonly IWebHelper _webHelper;
        private readonly IGeoCountryLookup _geoCountryLookup;
        private readonly IOverloadProtector _overloadProtector;

        public DefaultWorkContextSource(
            SmartDbContext db,
            IHttpContextAccessor httpContextAccessor,
            ILanguageResolver languageResolver,
            IStoreContext storeContext,
            ICustomerService customerService,
            Lazy<ITaxService> taxService,
            Lazy<ICurrencyService> currencyService,
            TaxSettings taxSettings,
            ICacheManager cache,
            IUserAgent userAgent,
            IWebHelper webHelper,
            IGeoCountryLookup geoCountryLookup,
            IOverloadProtector overloadProtector)
        {
            _db = db;
            _httpContextAccessor = httpContextAccessor;
            _languageResolver = languageResolver;
            _storeContext = storeContext;
            _customerService = customerService;
            _taxSettings = taxSettings;
            _taxService = taxService;
            _currencyService = currencyService;
            _userAgent = userAgent;
            _cache = cache;
            _webHelper = webHelper;
            _geoCountryLookup = geoCountryLookup;
            _overloadProtector = overloadProtector;
        }

        public ILogger Logger { get; set; } = NullLogger.Instance;

        public override async Task<HookResult> OnAfterSaveAsync(IHookedEntity entry, CancellationToken cancelToken)
        {
            if (entry.Entity is CustomerRole)
            {
                await _cache.RemoveByPatternAsync(CUSTOMERROLES_TAX_DISPLAY_TYPES_PATTERN_KEY);
                return HookResult.Ok;
            }
            else if (entry.Entity is Setting setting && entry.InitialState == EntityState.Modified)
            {
                if (setting.Name.EqualsNoCase(TypeHelper.NameOf<TaxSettings>(x => x.TaxDisplayType, true)))
                {
                    await _cache.RemoveByPatternAsync(CUSTOMERROLES_TAX_DISPLAY_TYPES_PATTERN_KEY); // depends on TaxSettings.TaxDisplayType
                }
                return HookResult.Ok;
            }
            else
            {
                return HookResult.Void;
            }
        }

        public async Task<(Customer, Customer)> ResolveCurrentCustomerAsync()
        {
            var context = new DetectCustomerContext
            {
                WorkContextSource = this,
                CustomerService = _customerService,
                Db = _db,
                HttpContext = _httpContextAccessor.HttpContext,
                UserAgent = _userAgent,
                WebHelper = _webHelper,
                OverloadProtector = _overloadProtector
            };

            try
            {
                var result = await ResolveCurrentCustomerCoreAsync(context);
                return result;
            }
            finally
            {
                await ReleaseIdentLockAsync(context);
            }
        }

        protected virtual async Task<(Customer, Customer)> ResolveCurrentCustomerCoreAsync(DetectCustomerContext context)
        {
            Customer customer = null;

            for (var i = 0; i < _customerDetectors.Length; i++)
            {
                var detector = _customerDetectors[i];
                customer = await detector(context);
                
                if (customer != null)
                {
                    if (customer.IsSystemAccount)
                    {
                        // Never check whether customer is deleted/inactive in this method.
                        // System accounts should neither be deletable nor activatable, they are mandatory.
                        return (customer, null);
                    }
                    else if (!customer.Deleted && customer.Active)
                    {
                        break;
                    }
                }
            }

            if (customer != null && context.HttpContext?.User?.Identity?.IsAuthenticated == true)
            {
                // Found authenticated customer.
                // Impersonate user if required (currently used for 'phone order' support).
                var impersonatedCustomer = await FindImpersonatedCustomerAsync(customer);
                return impersonatedCustomer != null
                    ? (impersonatedCustomer, customer)
                    : (customer, null);
            }

            if (customer != null)
            {
                if (customer.IsBot())
                {
                    // Check traffic limit for bots.
                    await CheckBotDeniedAsync(context);
                }
                else if (customer.IsGuest())
                {
                    // Check traffic limit for guests.
                    await CheckGuestDeniedAsync(context, customer);
                }
            }

            if (customer == null || (customer.IsGuest() && customer.IsRegistered()))
            {
                // No record yet or account deleted/deactivated.
                // Also dont' treat registered customers as guests.
                // Create new record in these cases.

                if (await context.OverloadProtector.ForbidNewGuestAsync(context.HttpContext))
                {
                    // DDoS attack? Prevent new guest session and throw to block the request.
                    throw new HttpResponseException(StatusCodes.Status403Forbidden, "Forbidden");
                }

                // Check traffic limit before creating new guest session.
                await CheckGuestDeniedAsync(context);

                // Create guest session
                customer = await CreateGuestCustomerAsync(context.ClientIdent);
            }

            return (customer, null);
        }

        protected virtual async Task<Customer> FindImpersonatedCustomerAsync(Customer customer)
        {
            var impersonatedCustomerId = customer.GenericAttributes.ImpersonatedCustomerId;
            if (impersonatedCustomerId > 0)
            {
                var impersonatedCustomer = await _db.Customers
                    .IncludeCustomerRoles()
                    .FindByIdAsync(impersonatedCustomerId.Value);

                if (impersonatedCustomer != null && !impersonatedCustomer.Deleted && impersonatedCustomer.Active)
                {
                    return impersonatedCustomer;
                }
            }

            return null;
        }

        protected virtual async Task<Customer> CreateGuestCustomerAsync(string clientIdent)
        {
            var customer = await _customerService.CreateGuestCustomerAsync(clientIdent, c =>
            {
                try
                {
                    c.LastIpAddress = _webHelper.GetClientIpAddress().ToString();
                    c.LastUserAgent = _userAgent.UserAgent.EmptyNull().Truncate(255);
                    c.LastUserDeviceType = _userAgent.Device.IsUnknown() ? _userAgent.Platform.Name : _userAgent.Device.Name;

                    var currentUrl = _webHelper.GetCurrentPageUrl(withQueryString: true);
                    c.LastVisitedPage = TrackActivityFilter.SanitizeUrl(ref currentUrl) 
                        ? currentUrl
                        : currentUrl.Truncate(2048);
                }
                catch
                {
                }
            });
            
            _customerService.AppendVisitorCookie(customer);

            return customer;
        }

        public async virtual Task<Language> ResolveWorkingLanguageAsync(Customer customer)
        {
            Guard.NotNull(customer);
            
            // Resolve the current working language
            var language = await _languageResolver.ResolveLanguageAsync(customer, _httpContextAccessor.HttpContext);

            // Set language if current customer langid does not match resolved language id
            var customerAttributes = customer.GenericAttributes;
            if (customerAttributes.LanguageId != language.Id)
            {
                await SaveCustomerAttribute(customer, SystemCustomerAttributeNames.LanguageId, language.Id, true);
            }

            return language;
        }

        public virtual async Task<Currency> ResolveWorkingCurrencyAsync(Customer customer, bool forAdminArea)
        {
            var query = _db.Currencies.AsNoTracking();

            Currency currency = null;

            // Return primary store currency when we're in admin area/mode
            if (forAdminArea)
            {
                currency = _currencyService.Value.PrimaryCurrency;
            }

            if (currency == null)
            {
                // Find current customer currency
                var storeCurrenciesMap = await query
                    .ApplyStandardFilter(false, _storeContext.CurrentStore.Id)
                    .ToDictionaryAsync(x => x.Id);

                if (customer != null && !customer.IsBot())
                {
                    // Bots should always crawl by primary store currency
                    var customerCurrencyId = customer.GenericAttributes.CurrencyId;
                    if (customerCurrencyId > 0)
                    {
                        if (storeCurrenciesMap.TryGetValue(customerCurrencyId.Value, out currency))
                        {
                            currency = VerifyCurrency(currency);
                            if (currency == null)
                            {
                                await SaveCustomerAttribute(customer, SystemCustomerAttributeNames.CurrencyId, null, true);
                            }
                        }
                    }
                }

                // if there's only one currency for current store it dominates the primary currency
                if (storeCurrenciesMap.Count == 1)
                {
                    currency = storeCurrenciesMap[storeCurrenciesMap.Keys.First()];
                }

                // Default currency of country to which the current IP address belongs.
                if (currency == null)
                {
                    var ipAddress = _webHelper.GetClientIpAddress();
                    var lookupCountry = _geoCountryLookup.LookupCountry(ipAddress);
                    if (lookupCountry != null)
                    {
                        var country = await _db.Countries
                            .AsNoTracking()
                            .Include(x => x.DefaultCurrency)
                            .ApplyIsoCodeFilter(lookupCountry.IsoCode)
                            .FirstOrDefaultAsync();

                        if (country?.DefaultCurrency?.Published == true)
                        {
                            currency = country.DefaultCurrency;
                        }
                    }
                }

                // Find currency by domain ending
                if (currency == null)
                {
                    var request = _httpContextAccessor.HttpContext?.Request;
                    if (request != null)
                    {
                        currency = storeCurrenciesMap.Values.GetByDomainEnding(request.Host.Value);
                    }
                }

                // Get default currency.
                if (currency == null)
                {
                    currency = VerifyCurrency(storeCurrenciesMap.Get(_storeContext.CurrentStore.DefaultCurrencyId));
                }

                // Get primary currency.
                if (currency == null)
                {
                    currency = VerifyCurrency(_currencyService.Value.PrimaryCurrency);
                }

                // Get the first published currency for current store
                if (currency == null)
                {
                    currency = storeCurrenciesMap.Values.FirstOrDefault();
                }
            }

            // If not found in currencies filtered by the current store, then return any currency
            if (currency == null)
            {
                currency = await query.ApplyStandardFilter().FirstOrDefaultAsync();
            }

            // No published currency available (fix it)
            if (currency == null)
            {
                currency = await query.AsTracking().ApplyStandardFilter(true).FirstOrDefaultAsync();
                if (currency != null)
                {
                    currency.Published = true;
                    await _db.SaveChangesAsync();
                }
            }

            return currency;
        }

        private static Currency VerifyCurrency(Currency currency)
        {
            if (currency != null && !currency.Published)
            {
                return null;
            }

            return currency;
        }

        public virtual async Task<TaxDisplayType> ResolveTaxDisplayTypeAsync(Customer customer, int storeId)
        {
            int? taxDisplayTypeId = null;

            if (_taxSettings.AllowCustomersToSelectTaxDisplayType && customer != null)
            {
                taxDisplayTypeId = customer.TaxDisplayTypeId;
            }

            if (taxDisplayTypeId == null && _taxSettings.EuVatEnabled)
            {
                if (customer != null && await _taxService.Value.IsVatExemptAsync(customer))
                {
                    taxDisplayTypeId = (int)TaxDisplayType.ExcludingTax;
                }
            }

            if (taxDisplayTypeId == null)
            {
                var customerRoles = customer.CustomerRoleMappings.Select(x => x.CustomerRole).ToList();
                string cacheKey = string.Format(CUSTOMERROLES_TAX_DISPLAY_TYPES_KEY, string.Join(",", customerRoles.Select(x => x.Id)), storeId);
                var cacheResult = _cache.Get(cacheKey, () =>
                {
                    var roleTaxDisplayTypes = customerRoles
                        .Where(x => x.TaxDisplayType.HasValue)
                        .OrderByDescending(x => x.TaxDisplayType.Value)
                        .Select(x => x.TaxDisplayType.Value);

                    if (roleTaxDisplayTypes.Any())
                    {
                        return (TaxDisplayType)roleTaxDisplayTypes.FirstOrDefault();
                    }

                    return _taxSettings.TaxDisplayType;
                });

                taxDisplayTypeId = (int)cacheResult;
            }

            return (TaxDisplayType)taxDisplayTypeId.Value;
        }

        public Task SaveCustomerAttribute(Customer customer, string name, int? value, bool async)
        {
            Guard.NotNull(customer);
            Guard.NotEmpty(name);

            if (customer.IsSystemAccount)
            {
                return Task.CompletedTask;
            }

            var attributes = customer.GenericAttributes;
            attributes.Set(name, value, attributes.CurrentStoreId);

            if (async)
            {
                return attributes.SaveChangesAsync();
            }
            else
            {
                attributes.SaveChanges();
                return Task.CompletedTask;
            }
        }
        
        #region Customer resolvers

        protected class DetectCustomerContext
        {
            public DefaultWorkContextSource WorkContextSource { get; init; }
            public HttpContext HttpContext { get; init; }
            public SmartDbContext Db { get; init; }
            public ICustomerService CustomerService { get; init; }
            public IUserAgent UserAgent { get; init; }
            public IWebHelper WebHelper { get; init; }
            
            public IOverloadProtector OverloadProtector { get; init; }
            public bool? DenyGuest { get; set; }
            public bool? DenyBot { get; set; }

            public Guid? CustomerGuid { get; set; }
            public string ClientIdent { get; set; }
            public ILockHandle LockHandle { get; set; }
        }

        private static Task<Customer> DetectAuthenticated(DetectCustomerContext context)
        {
            return context.CustomerService.GetAuthenticatedCustomerAsync();
        }

        private static Task<Customer> DetectTaskScheduler(DetectCustomerContext context)
        {
            if (context.HttpContext != null && context.HttpContext.Request.IsCalledByTaskScheduler())
            {
                return context.CustomerService.GetCustomerBySystemNameAsync(SystemCustomerNames.BackgroundTask);
            }
            
            return Task.FromResult<Customer>(null);
        }

        private static Task<Customer> DetectPdfConverter(DetectCustomerContext context)
        {
            if (context.UserAgent.IsPdfConverter())
            {
                return context.CustomerService.GetCustomerBySystemNameAsync(SystemCustomerNames.PdfConverter);
            }

            return Task.FromResult<Customer>(null);
        }

        private static async Task<Customer> DetectBot(DetectCustomerContext context)
        {
            if (context.UserAgent.IsBot())
            {
                return await context.CustomerService.GetCustomerBySystemNameAsync(SystemCustomerNames.Bot);
            }

            return null;
        }

        private static async Task<Customer> DetectWebhookEndpoint(DetectCustomerContext context)
        {
            if (context.HttpContext is HttpContext httpContext)
            {
                var isWebhook = httpContext.GetEndpoint()?.Metadata?.GetMetadata<WebhookEndpointAttribute>() != null;
                if (!isWebhook && httpContext.Response.StatusCode == StatusCodes.Status401Unauthorized)
                {
                    isWebhook = httpContext.Features.Get<IExceptionHandlerPathFeature>()?.Path?.StartsWithNoCase("/odata/") ?? false;
                }

                if (isWebhook)
                {
                    var customer = await context.CustomerService.GetCustomerBySystemNameAsync(SystemCustomerNames.WebhookClient);
                    if (customer == null)
                    {
                        customer = await context.CustomerService.CreateGuestCustomerAsync(null, c =>
                        {
                            c.Email = "builtin@webhook-client.com";
                            c.AdminComment = "Built-in system record used for webhook clients.";
                            c.IsSystemAccount = true;
                            c.SystemName = SystemCustomerNames.WebhookClient;
                        });
                    }

                    return customer;
                }
            }
            
            return null;
        }

        private static async Task<Customer> DetectGuest(DetectCustomerContext context)
        {
            var visitorCookie = context.HttpContext?.Request?.Cookies[CookieNames.Visitor];
            if (visitorCookie != null && Guid.TryParse(visitorCookie, out var customerGuid))
            {
                context.CustomerGuid = customerGuid;
                
                // Cookie present. Try to load guest customer by its value.
                var customer = await context.Db.Customers
                    //.IncludeShoppingCart()
                    .IncludeCustomerRoles()
                    .Where(c => c.CustomerGuid == customerGuid)
                    .FirstOrDefaultAsync();

                // Don't treat registered customers as guests.
                if (customer != null && !customer.IsRegistered())
                {
                    return customer;
                }
            }

            return null;
        }

        //private static async Task<Customer> DetectBotForMedia(DetectCustomerContext context)
        //{
        //    // Don't overstress the system with guest detection for media files.
        //    // If there's no endpoint, it's most likely a media file request.
        //    // Bad bots don't accept cookies anyway. If there is no visitor cookie, it's a bot.
        //    if (context.CustomerGuid == null && context.HttpContext.GetEndpoint() == null)
        //    {
        //        if (context.UserAgent.IsBot())
        //        {
        //            await CheckBotDeniedAsync(context);
        //        }
        //        else
        //        {
        //            await CheckGuestDeniedAsync(context);
        //        }

        //        return await context.CustomerService.GetCustomerBySystemNameAsync(SystemCustomerNames.Bot);
        //    }

        //    return null;
        //}

        private static async Task<Customer> DetectByClientIdent(DetectCustomerContext context)
        {
            // No anonymous visitor cookie yet. Try to identify anyway (by IP and UserAgent combination).
            var clientIdent = context.WebHelper.GetClientIdent();
            context.ClientIdent = clientIdent;

            var customer = await context.CustomerService.FindCustomerByClientIdentAsync(clientIdent, maxAgeSeconds: 300);

            if (customer == null)
            {
                // Prevent another parallel request to recreate the guest customer with same client ident.
                var lockProvider = context.HttpContext.RequestServices.GetRequiredService<IDistributedLockProvider>();
                var @lock = lockProvider.GetLock("clientident:" + clientIdent);

                if ((await @lock.TryAcquireAsync(TimeSpan.FromSeconds(2))).Out(out var lockHandle))
                {
                    context.LockHandle = lockHandle;

                    // Try again after lock acquisition. Guest account may have been created in the meantime.
                    customer = await context.CustomerService.FindCustomerByClientIdentAsync(clientIdent, maxAgeSeconds: 300);
                }
            }

            if (customer != null)
            {
                if (customer.IsRegistered() || !customer.IsGuest())
                {
                    // Ignore registered and non-guest accounts.
                    return null;
                }
                else
                {
                    customer.DetectedByClientIdent = true;

                    // Try to append visitor cookie to better identify visitor on next (sub)-request
                    context.CustomerService.AppendVisitorCookie(customer);
                }
            }

            return customer;
        }

        private static async Task ReleaseIdentLockAsync(DetectCustomerContext context)
        {
            if (context.LockHandle != null)
            {
                await context.LockHandle.ReleaseAsync();
                context.LockHandle = null;
            }
        }

        private static async Task CheckGuestDeniedAsync(DetectCustomerContext context, Customer customer = null)
        {
            context.DenyGuest ??= !context.HttpContext.Request.IsSubRequest() && await context.OverloadProtector.DenyGuestAsync(context.HttpContext, customer);
            if (context.DenyGuest == true)
            {
                throw new HttpResponseException(StatusCodes.Status429TooManyRequests, "Too many requests");
            }
        }

        private static async Task CheckBotDeniedAsync(DetectCustomerContext context)
        {
            context.DenyBot ??= !context.HttpContext.Request.IsSubRequest() && await context.OverloadProtector.DenyBotAsync(context.HttpContext, context.UserAgent);
            if (context.DenyBot == true)
            {
                throw new HttpResponseException(StatusCodes.Status429TooManyRequests, "Too many requests");
            }
        }

        #endregion
    }
}
