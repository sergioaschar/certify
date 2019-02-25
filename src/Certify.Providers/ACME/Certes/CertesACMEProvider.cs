﻿using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Security.Cryptography.X509Certificates;
using System.Threading;
using System.Threading.Tasks;
using Certes;
using Certes.Acme;
using Certes.Acme.Resource;
using Certify.Models;
using Certify.Models.Config;
using Certify.Models.Plugins;
using Certify.Models.Providers;

namespace Certify.Providers.Certes
{
    /// <summary>
    /// Certes Provider settings for serialization
    /// </summary>
    public class CertesSettings
    {
        public string AccountEmail { get; set; }
        public string AccountUri { get; set; }
        public string AccountKey { get; set; }
    }


#pragma warning disable IDE1006 // Naming Styles
    public class DiagEcKey
    {
        public string kty { get; set; }
        public string crv { get; set; }
        public string x { get; set; }
        public string y { get; set; }
    }
#pragma warning restore IDE1006 // Naming Styles

    // used to diagnose account key faults
    public class DiagAccountInfo
    {
        public int ID { get; set; }
        public DiagEcKey Key { get; set; }
    }

    public class LoggingHandler : DelegatingHandler
    {
        public DiagAccountInfo DiagAccountInfo { get; set; }
        private ILog _log = null;

        public LoggingHandler(HttpMessageHandler innerHandler, ILog log)
            : base(innerHandler)
        {
            _log = log;
        }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            if (_log != null)
            {
                _log.Debug($"Http Request: {request.ToString()}");
                if (request.Content != null)
                {
                    _log.Debug(await request.Content.ReadAsStringAsync());
                }
            }

            var response = await base.SendAsync(request, cancellationToken);

            if (_log != null)
            {
                _log.Debug($"Http Response: {response.ToString()}");

                if (response.Content != null)
                {
                    _log.Debug(await response.Content.ReadAsStringAsync());
                }
            }

            return response;
        }
    }

    /// <summary>
    /// ACME Provider using certes https://github.com/fszlin/certes
    /// </summary>
    public class CertesACMEProvider : IACMEClientProvider, IVaultProvider
    {
        private AcmeContext _acme;
#if DEBUG
        private readonly Uri _serviceUri = WellKnownServers.LetsEncryptStagingV2;
#else
        private readonly Uri _serviceUri = WellKnownServers.LetsEncryptV2;
#endif

        private readonly string _settingsFolder = null;

        private CertesSettings _settings = null;
        private Dictionary<string, IOrderContext> _currentOrders;
        private IdnMapping _idnMapping = new IdnMapping();
        private DateTime _lastInitDateTime = new DateTime();
        private readonly bool _newContactUseCurrentAccountKey = false;

        private AcmeHttpClient _httpClient;
        private LoggingHandler _loggingHandler;

        private readonly string _userAgentName = "Certify SSL Manager";
        private ILog _log = null;

        public CertesACMEProvider(string settingsPath, string userAgentName)
        {
            _settingsFolder = settingsPath;

            var certesAssembly = typeof(AcmeContext).Assembly.GetName();

            _userAgentName = $"{userAgentName} {certesAssembly.Name}/{certesAssembly.Version.ToString()}";
        }

        public string GetProviderName() => "Certes";

        public string GetAcmeBaseURI() => _acme.DirectoryUri.ToString();

        public async Task<Uri> GetAcmeTermsOfService() => await _acme.TermsOfService();

        /// <summary>
        /// Initialise provider settings, loading current account key if present
        /// </summary>
        public async Task<bool> InitProvider(ILog log = null)
        {
            if (log != null)
            {
                _log = log;
            }

            _lastInitDateTime = DateTime.Now;

            _loggingHandler = new LoggingHandler(new HttpClientHandler(), _log);
            var customHttpClient = new System.Net.Http.HttpClient(_loggingHandler);
            customHttpClient.DefaultRequestHeaders.Add("User-Agent", _userAgentName);

            _httpClient = new AcmeHttpClient(_serviceUri, customHttpClient);

            LoadSettings();

            if (!string.IsNullOrEmpty(_settings.AccountKey))
            {
                if (System.IO.File.Exists(_settingsFolder + "\\c-acc.key"))
                {
                    //remove legacy key info
                    System.IO.File.Delete(_settingsFolder + "\\c-acc.key");
                }
                SetAcmeContextAccountKey(_settings.AccountKey);
            }
            else
            {
                // no account key in settings, check .key (legacy key file)
                if (System.IO.File.Exists(_settingsFolder + "\\c-acc.key"))
                {
                    var pem = System.IO.File.ReadAllText(_settingsFolder + "\\c-acc.key");
                    SetAcmeContextAccountKey(pem);
                }
            }

            _currentOrders = new Dictionary<string, IOrderContext>();

            return await Task.FromResult(true);
        }

        private async Task<string> CheckAcmeAccount()
        {
            // check our current account ID and key match the values LE expects
            if (_acme == null)
            {
                return "none";
            }

            try
            {
                var accountContext = await _acme.Account();
                var account = await accountContext.Resource();

                if (account.Status == AccountStatus.Valid)
                {
                    if (account.TermsOfServiceAgreed == false)
                    {
                        return "tos-required";
                    }
                    else
                    {
                        // all good
                        return "ok";
                    }
                }
                else
                {
                    if (account.Status == AccountStatus.Revoked)
                    {
                        return "account-revoked";
                    }

                    if (account.Status == AccountStatus.Deactivated)
                    {
                        return "account-deactivated";
                    }
                }

                return "unknown";
            }
            catch (AcmeRequestException exp)
            {
                if (exp.Error.Type == "urn:ietf:params:acme:error:accountDoesNotExist")
                {
                    return "account-doesnotexist";
                }
                else
                {
                    return "account-error";
                }
            }
            catch (Exception)
            {
                // we failed to check the account status, probably because of connectivity. Assume OK
                return "ok";
            }
        }

        public async Task<bool> ChangeAccountKey(ILog log)
        {
            if (_acme == null)
            {
                if (log != null) log.Error("No account context. Cannot update account key.");
                return false;
            }
            else
            {
                // allocate new key and inform LE of key change
                // same default key type as certes
                var newKey = KeyFactory.NewKey(KeyAlgorithm.ES256);
                var account = await _acme.ChangeKey(newKey);
                _settings.AccountKey = newKey.ToPem();
                var savedOK = await SaveSettings();

                ArchiveAccountKey(await _acme.Account());

                return savedOK;
            }
        }

        /// <summary>
        /// Load provider settings or create new
        /// </summary>
        private void LoadSettings()
        {
            if (!System.IO.Directory.Exists(_settingsFolder))
            {
                System.IO.Directory.CreateDirectory(_settingsFolder);
            }

            if (System.IO.File.Exists(_settingsFolder + "\\c-settings.json"))
            {
                var json = System.IO.File.ReadAllText(_settingsFolder + "\\c-settings.json");
                _settings = Newtonsoft.Json.JsonConvert.DeserializeObject<CertesSettings>(json);
            }
            else
            {
                _settings = new CertesSettings();
            }
        }

        private async Task<bool> WriteAllTextAsync(string path, string content)
        {
            try
            {
                using (var fs = File.CreateText(path))
                {
                    await fs.WriteAsync(content);
                    await fs.FlushAsync();
                }

                // artificial delay for flush to really complete (just begin superstitious)
                await Task.Delay(250);
            }
            catch (Exception)
            {
                return false;
            }

            return true;
        }

        /// <summary>
        /// Save current provider settings
        /// </summary>
        private async Task<bool> SaveSettings()
        {
            return await WriteAllTextAsync(_settingsFolder + "\\c-settings.json", Newtonsoft.Json.JsonConvert.SerializeObject(_settings));
        }

        /// <summary>
        /// Save the current account key
        /// </summary>
        /// <returns>  </returns>
        private bool ArchiveAccountKey(IAccountContext accountContext)
        {
            var pem = _acme.AccountKey.ToPem();

            _settings.AccountKey = pem;
            _settings.AccountUri = accountContext.Location.ToString();

            var entry = $"\r\n\r{DateTime.Now}\r\n{ _settings.AccountUri ?? "" }\r\n{_settings.AccountEmail ?? ""}\r\n{_settings.AccountKey}";

            // archive account id history
            System.IO.File.AppendAllText(_settingsFolder + "\\c-acc-archive", entry);

            return true;
        }

        /// <summary>
        /// Determine if we have a currently registered account with the ACME CA (Let's Encrypt)
        /// </summary>
        /// <returns>  </returns>
        public bool IsAccountRegistered()
        {
            if (!string.IsNullOrEmpty(_settings.AccountEmail))
            {
                return true;
            }
            else
            {
                return false;
            }
        }

        /// <summary>
        /// Set a new account key from PEM encoded text
        /// </summary>
        /// <param name="pem">  </param>
        private void SetAcmeContextAccountKey(string pem)
        {
            var accountkey = KeyFactory.FromPem(pem);

            _acme = new AcmeContext(_serviceUri, accountkey, _httpClient);

            if (_settings.AccountKey != pem) _settings.AccountKey = pem;
        }

        /// <summary>
        /// Register a new account with the ACME CA (Let's Encrypt), accepting terms and conditions
        /// </summary>
        /// <param name="log">  </param>
        /// <param name="email">  </param>
        /// <returns>  </returns>
        public async Task<bool> AddNewAccountAndAcceptTOS(ILog log, string email)
        {
            try
            {
                IKey accKey = null;

                if (_newContactUseCurrentAccountKey && !string.IsNullOrEmpty(_settings.AccountKey))
                {
                    accKey = KeyFactory.FromPem(_settings.AccountKey);
                }

                // start new account context, create new account (with new key, if not enabled)
                _acme = new AcmeContext(_serviceUri, accKey, _httpClient);
                var account = await _acme.NewAccount(email, true);

                _settings.AccountEmail = email;

                // archive account key and update current settings with new ACME account key and account URI
                var keyUpdated = ArchiveAccountKey(account);
                var settingsSaved = await SaveSettings();

                if (keyUpdated && settingsSaved)
                {
                    log.Information($"Registering account {email} with certificate authority");

                    // re-init provider based on new account key
                    await InitProvider(null);

                    return true;
                }
                else
                {
                    throw new Exception($"Failed to save account settings: keyUpdate:{keyUpdated} settingsSaved:{settingsSaved}");
                }
            }
            catch (Exception exp)
            {
                log.Error($"Failed to register account {email} with certificate authority: {exp.Message}");
                return false;
            }
        }

        /// <summary>
        /// Begin order for new certificate for one or more domains, fetching the required challenges
        /// to complete
        /// </summary>
        /// <param name="log">  </param>
        /// <param name="config">  </param>
        /// <param name="orderUri"> Uri of existing order to resume </param>
        /// <returns>  </returns>
        public async Task<PendingOrder> BeginCertificateOrder(ILog log, CertRequestConfig config, string orderUri = null)
        {
            if (DateTime.Now.Subtract(_lastInitDateTime).TotalMinutes > 30)
            {
                // our acme context nonce may have expired (which returns "JWS has an invalid
                // anti-replay nonce") so start a new one
                await InitProvider(null);
            }

            var pendingOrder = new PendingOrder { IsPendingAuthorizations = true };

            // prepare a list of all pending authorization we need to complete, or those we have
            // already satisfied
            var authzList = new List<PendingAuthorization>();

            //if no alternative domain specified, use the primary domain as the subject
            var domainOrders = new List<string>
            {
                // order all of the distinct domains in the config (primary + SAN).
                _idnMapping.GetAscii(config.PrimaryDomain)
            };

            if (config.SubjectAlternativeNames != null)
            {
                foreach (var s in config.SubjectAlternativeNames)
                {
                    if (!domainOrders.Contains(s))
                    {
                        domainOrders.Add(_idnMapping.GetAscii(s));
                    }
                }
            }

            try
            {
                IOrderContext order = null;
                var remainingAttempts = 3;
                var orderCreated = false;
                try
                {
                    while (!orderCreated && remainingAttempts > 0)
                    {
                        try
                        {
                            remainingAttempts--;

                            log.Error($"BeginCertificateOrder: creating/retrieving order. Retries remaining:{remainingAttempts} ");

                            if (orderUri != null)
                            {
                                order = _acme.Order(new Uri(orderUri));
                            }
                            else
                            {
                                order = await _acme.NewOrder(domainOrders);
                            }

                            if (order != null)
                            {
                                orderCreated = true;
                            }
                        }
                        catch (Exception exp)
                        {
                            remainingAttempts--;

                            log.Error($"BeginCertificateOrder: error creating order. Retries remaining:{remainingAttempts} {exp.ToString()} ");

                            if (remainingAttempts == 0)
                            {
                                // all attempts to create order failed
                                throw;
                            }
                            else
                            {
                                await Task.Delay(1000);
                            }
                        }
                    }
                }
                catch (NullReferenceException exp)
                {
                    var msg = $"Failed to begin certificate order (account problem or API is not currently available): {exp}";

                    log.Error(msg);

                    pendingOrder.Authorizations =
                    new List<PendingAuthorization> {
                    new PendingAuthorization
                    {
                        AuthorizationError = msg,
                        IsFailure=true
                    }
                   };

                    return pendingOrder;
                }

                if (order == null) throw new Exception("Failed to begin certificate order.");

                orderUri = order.Location.ToString();

                pendingOrder.OrderUri = orderUri;

                log.Information($"Created ACME Order: {orderUri}");

                // track order in memory, keyed on order Uri
                if (_currentOrders.Keys.Contains(orderUri))
                {
                    _currentOrders.Remove(orderUri);
                }

                _currentOrders.Add(orderUri, order);

                // handle order status 'Ready' if all authorizations are already valid
                var orderDetails = await order.Resource();
                if (orderDetails.Status == OrderStatus.Ready)
                {
                    pendingOrder.IsPendingAuthorizations = false;
                }

                // get all required pending (or already valid) authorizations for this order

                log.Information($"Fetching Authorizations.");

                var orderAuthorizations = await order.Authorizations();

                // get the challenges for each authorization
                foreach (var authz in orderAuthorizations)
                {
                    log.Debug($"Fetching Authz Challenges.");

                    var allChallenges = await authz.Challenges();
                    var res = await authz.Resource();
                    var authzDomain = res.Identifier.Value;
                    if (res.Wildcard == true) authzDomain = "*." + authzDomain;

                    var challenges = new List<AuthorizationChallengeItem>();

                    // add http challenge (if any)
                    var httpChallenge = await authz.Http();
                    if (httpChallenge != null)
                    {
                        var httpChallengeStatus = await httpChallenge.Resource();

                        log.Information($"Got http-01 challenge {httpChallengeStatus.Url}");

                        if (httpChallengeStatus.Status == ChallengeStatus.Invalid)
                        {
                            log.Error($"HTTP challenge has an invalid status");
                        }

                        challenges.Add(new AuthorizationChallengeItem
                        {
                            ChallengeType = SupportedChallengeTypes.CHALLENGE_TYPE_HTTP,
                            Key = httpChallenge.Token,
                            Value = httpChallenge.KeyAuthz,
                            ChallengeData = httpChallenge,
                            ResourceUri = $"http://{authzDomain.Replace("*.", "")}/.well-known/acme-challenge/{httpChallenge.Token}",
                            ResourcePath = $".well-known\\acme-challenge\\{httpChallenge.Token}",
                            IsValidated = (httpChallengeStatus.Status == ChallengeStatus.Valid)
                        });
                    }

                    // add dns challenge (if any)
                    var dnsChallenge = await authz.Dns();
                    if (dnsChallenge != null)
                    {
                        var dnsChallengeStatus = await dnsChallenge.Resource();

                        log.Information($"Got dns-01 challenge {dnsChallengeStatus.Url}");

                        if (dnsChallengeStatus.Status == ChallengeStatus.Invalid)
                        {
                            log.Error($"DNS challenge has an invalid status");
                        }

                        var dnsValue = _acme.AccountKey.DnsTxt(dnsChallenge.Token); //ComputeDnsValue(dnsChallenge, _acme.AccountKey);
                        var dnsKey = $"_acme-challenge.{authzDomain}".Replace("*.", "");

                        challenges.Add(new AuthorizationChallengeItem
                        {
                            ChallengeType = SupportedChallengeTypes.CHALLENGE_TYPE_DNS,
                            Key = dnsKey,
                            Value = dnsValue,
                            ChallengeData = dnsChallenge,
                            IsValidated = (dnsChallengeStatus.Status == ChallengeStatus.Valid)
                        });
                    }

                    // report back on the challenges we now may need to attempt
                    authzList.Add(
                     new PendingAuthorization
                     {
                         Challenges = challenges,
                         Identifier = new IdentifierItem
                         {
                             Dns = authzDomain,
                             IsAuthorizationPending = !challenges.Any(c => c.IsValidated) //auth is pending if we have no challenges already validated
                         },
                         AuthorizationContext = authz,
                         IsValidated = challenges.Any(c => c.IsValidated),
                         OrderUri = orderUri
                     });
                }

                pendingOrder.Authorizations = authzList;

                return pendingOrder;
            }
            catch (AcmeRequestException exp)
            {
                // failed to register one or more domain identifier with LE (invalid, rate limit or
                // CAA fail?)

                var msg = $"Failed to begin certificate order: {exp.Error?.Detail}";

                log.Error(msg);

                pendingOrder.Authorizations =
                new List<PendingAuthorization> {
                    new PendingAuthorization
                    {
                        AuthorizationError = msg,
                        IsFailure=true
                    }
               };

                return pendingOrder;
            }
        }

        private string GetExceptionMessage(Exception exp)
        {
            var msg = exp.Message;

            if (exp.InnerException != null)
            {
                if (exp.InnerException is AcmeRequestException)
                {
                    msg += ":: " + ((AcmeRequestException)exp.InnerException).Error.Detail;
                }
                else
                {
                    msg += ":: " + exp.InnerException.Message;
                }
            }

            return msg;
        }

        /// <summary>
        /// if not already validate, ask ACME CA to check we have answered the nominated challenges correctly
        /// </summary>
        /// <param name="log">  </param>
        /// <param name="challengeType">  </param>
        /// <param name="attemptedChallenge">  </param>
        /// <returns>  </returns>
        public async Task<StatusMessage> SubmitChallenge(ILog log, string challengeType, AuthorizationChallengeItem attemptedChallenge)
        {
            if (!attemptedChallenge.IsValidated)
            {
                var challenge = (IChallengeContext)attemptedChallenge.ChallengeData;
                try
                {
                    var result = await challenge.Validate();

                    var attempts = 10;

                    while (attempts > 0 && result.Status == ChallengeStatus.Pending || result.Status == ChallengeStatus.Processing)
                    {
                        result = await challenge.Resource();
                    }

                    if (result.Status == ChallengeStatus.Valid)
                    {
                        return new StatusMessage
                        {
                            IsOK = true,
                            Message = "Submitted"
                        };
                    }
                    else
                    {
                        var challengeError = await challenge.Resource();
                        return new StatusMessage
                        {
                            IsOK = false,
                            Message = challengeError.Error?.Detail
                        };
                    }
                }
                catch (AcmeRequestException exp)
                {
                    var msg = $"Submit Challenge failed: {exp.Error?.Detail}";

                    log.Error(msg);

                    return new StatusMessage
                    {
                        IsOK = false,
                        Message = msg
                    };
                }
            }
            else
            {
                return new StatusMessage
                {
                    IsOK = true,
                    Message = "Validated"
                };
            }
        }

        /// <summary>
        /// After we have asked the CA to check we have responded to the required challenges, check
        /// the result to see if they are now valid
        /// </summary>
        /// <param name="log">  </param>
        /// <param name="challengeType">  </param>
        /// <param name="pendingAuthorization">  </param>
        /// <returns>  </returns>
        public async Task<PendingAuthorization> CheckValidationCompleted(ILog log, string challengeType, PendingAuthorization pendingAuthorization)
        {
            var authz = (IAuthorizationContext)pendingAuthorization.AuthorizationContext;

            var res = await authz.Resource();

            var attempts = 20;
            while (attempts > 0 && (res.Status != AuthorizationStatus.Valid && res.Status != AuthorizationStatus.Invalid))
            {
                res = await authz.Resource();

                attempts--;

                // if status is not yet valid or invalid, wait a sec and try again
                if (res.Status != AuthorizationStatus.Valid && res.Status != AuthorizationStatus.Invalid)
                {
                    await Task.Delay(1000);
                }
            }

            if (res.Status == AuthorizationStatus.Valid)
            {
                pendingAuthorization.Identifier.IsAuthorizationPending = false;
                pendingAuthorization.Identifier.Status = "valid";
                pendingAuthorization.IsValidated = true;
            }
            else
            {
                pendingAuthorization.Identifier.Status = "invalid";

                //determine error
                try
                {
                    var challenge = res.Challenges.FirstOrDefault(c => c.Type == challengeType);
                    if (challenge != null)
                    {
                        var r = await _acme.HttpClient.Get<AcmeResponse<Challenge>>(challenge.Url);

                        pendingAuthorization.AuthorizationError = $"{r.Resource.Error.Detail} {r.Resource.Error.Status} {r.Resource.Error.Type}";
                    }
                }
                catch
                {
                    log.Warning("Failed to determine error message for failed authorization.");
                }
                pendingAuthorization.Identifier.ValidationError = "Failed";
                pendingAuthorization.Identifier.ValidationErrorType = "Error";
                pendingAuthorization.IsValidated = false;
            }
            return pendingAuthorization;
        }

        /// <summary>
        /// Once validation has completed for our requested domains we can complete the certificate
        /// request by submitting a Certificate Signing Request (CSR) to the CA
        /// </summary>
        /// <param name="log">  </param>
        /// <param name="primaryDnsIdentifier">  </param>
        /// <param name="alternativeDnsIdentifiers">  </param>
        /// <param name="config">  </param>
        /// <returns>  </returns>
        public async Task<ProcessStepResult> CompleteCertificateRequest(ILog log, CertRequestConfig config, string orderId)
        {
            var orderContext = _currentOrders[orderId];

            // check order status, if it's not 'ready' then try a few more times before giving up
            var order = await orderContext.Resource();

            var attempts = 5;
            while (attempts > 0 && order?.Status != OrderStatus.Ready)
            {
                await Task.Delay(2000);
                order = await orderContext.Resource();
                attempts--;
            }

            if (order?.Status != OrderStatus.Ready)
            {
                return new ProcessStepResult { IsSuccess = false, ErrorMessage = "Certificate Request did not complete. Order did not reach Ready status in the time allowed.", Result = order };
            }

            // generate temp keypair for signing CSR
            var keyAlg = KeyAlgorithm.RS256;

            if (!string.IsNullOrEmpty(config.CSRKeyAlg))
            {
                if (config.CSRKeyAlg == "RS256") keyAlg = KeyAlgorithm.RS256;
                if (config.CSRKeyAlg == "ECDSA256") keyAlg = KeyAlgorithm.ES256;
                if (config.CSRKeyAlg == "ECDSA384") keyAlg = KeyAlgorithm.ES384;
                if (config.CSRKeyAlg == "ECDSA521") keyAlg = KeyAlgorithm.ES512;
            }

            var csrKey = KeyFactory.NewKey(keyAlg);

            var certFriendlyName = $"{config.PrimaryDomain} [Certify] ";

            // generate cert
            CertificateChain certificateChain = null;
            DateTime? certExpiration = null;
            try
            {
                certificateChain = await orderContext.Generate(new CsrInfo
                {
                    CommonName = _idnMapping.GetAscii(config.PrimaryDomain)
                }, csrKey);

                var cert = new X509Certificate2(certificateChain.Certificate.ToDer());
                certExpiration = cert.NotAfter;
                certFriendlyName += $"{cert.GetEffectiveDateString()} to {cert.GetExpirationDateString()}";
            }
            catch (AcmeRequestException exp)
            {
                var msg = $"Failed to finalize certificate order:  {exp.Error?.Detail}";
                log.Error(msg);

                return new ProcessStepResult { ErrorMessage = msg, IsSuccess = false, Result = exp.Error };
            }

            // file will be named as {expiration yyyyMMdd}_{guid} e.g. 20290301_4fd1b2ea-7b6e-4dca-b5d9-e0e7254e568b
            var certId = certExpiration.Value.ToString("yyyyMMdd") + "_" + Guid.NewGuid().ToString().Substring(0, 8);

            var domainAsPath = config.PrimaryDomain.Replace("*", "_");

            var pfxPath = ExportFullCertPFX(certFriendlyName, csrKey, certificateChain, certId, domainAsPath);

            ExportFullCertPEM(csrKey, certificateChain, certId, domainAsPath);

            return new ProcessStepResult { IsSuccess = true, Result = pfxPath };
        }

        private string ExportFullCertPFX(string certFriendlyName, IKey csrKey, CertificateChain certificateChain, string certId, string primaryDomainPath)
        {
            var storePath = Path.GetFullPath(Path.Combine(new string[] { _settingsFolder, "..\\assets", primaryDomainPath }));

            if (!System.IO.Directory.Exists(storePath))
            {
                System.IO.Directory.CreateDirectory(storePath);
            }

            var pfxFile = certId + ".pfx";
            var pfxPath = Path.Combine(storePath, pfxFile);

            var pfx = certificateChain.ToPfx(csrKey);
            var pfxBytes = pfx.Build(certFriendlyName, "");

            System.IO.File.WriteAllBytes(pfxPath, pfxBytes);
            return pfxPath;
        }

        private string ExportFullCertPEM(IKey csrKey, CertificateChain certificateChain, string certId, string primaryDomainPath)
        {
            var storePath = Path.GetFullPath(Path.Combine(new string[] { _settingsFolder, "..\\assets", primaryDomainPath }));

            if (!System.IO.Directory.Exists(storePath))
            {
                System.IO.Directory.CreateDirectory(storePath);
            }

            if (!System.IO.Directory.Exists(storePath))
            {
                System.IO.Directory.CreateDirectory(storePath);
            }

            var pemPath = Path.Combine(storePath, certId + ".pem");

            // write pem in order of Private .key, primary server .crt, intermediate .crt, issuer.crt
            // note:
            // nginx needs combined primary + intermediate.crt as pem (ssl_certificate), plus .key (ssl_certificate_key)
            // apache needs combined primary.crt (SSLCertificateFile), intermediate.crt (SSLCertificateChainFile), plus private .key (SSLCertificateKeyFile)
            var pem = certificateChain.ToPem(csrKey);

            System.IO.File.WriteAllText(pemPath, pem);

            return pemPath;
        }

        public async Task<StatusMessage> RevokeCertificate(ILog log, ManagedCertificate managedCertificate)
        {
            // get current PFX, extract DER bytes
            try
            {
                var pkcs = new Org.BouncyCastle.Pkcs.Pkcs12Store(File.Open(managedCertificate.CertificatePath, FileMode.Open), "".ToCharArray());

                var certAliases = pkcs.Aliases.GetEnumerator();
                certAliases.MoveNext();

                var certEntry = pkcs.GetCertificate(certAliases.Current.ToString());
                var certificate = certEntry.Certificate;

                // revoke certificate
                var der = certificate.GetEncoded();
                await _acme.RevokeCertificate(der, RevocationReason.Unspecified, null);
            }
            catch (Exception exp)
            {
                return new StatusMessage { IsOK = false, Message = $"Failed to revoke certificate: {exp.Message}" };
            }

            return new StatusMessage { IsOK = true, Message = "Certificate revoked" };
        }

        public List<RegistrationItem> GetContactRegistrations()
        {
            var list = new List<RegistrationItem>();
            if (IsAccountRegistered())
            {
                list.Add(new RegistrationItem { Name = _settings.AccountEmail });
            }
            return list;
        }

        public void DeleteContactRegistration(string id)
        {
            // do nothing for this provider
        }

        public void EnableSensitiveFileEncryption()
        {
            //FIXME: not implemented
        }

        public Task<string> GetAcmeAccountStatus() => throw new NotImplementedException();
    }
}
