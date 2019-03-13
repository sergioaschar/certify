﻿using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading.Tasks;
using Certify.Core.Management.DeploymentTasks;
using Certify.Models;
using Certify.Models.Providers;
using Certify.Providers.DeploymentTasks;

namespace Certify.Management
{
    public partial class CertifyManager
    {
        public event Action<ManagedCertificate> OnManagedCertificateUpdated;

        public async Task<ManagedCertificate> GetManagedCertificate(string id) => await _itemManager.GetManagedCertificate(id);

        public async Task<List<ManagedCertificate>> GetManagedCertificates(ManagedCertificateFilter filter = null) => await _itemManager.GetManagedCertificates(filter, true);

        public async Task<ManagedCertificate> UpdateManagedCertificate(ManagedCertificate site)
        {
            site = await _itemManager.UpdatedManagedCertificate(site);

            // report request state to status hub clients
            OnManagedCertificateUpdated?.Invoke(site);
            return site;
        }

        private async Task UpdateManagedCertificateStatus(ManagedCertificate managedCertificate, RequestState status,
            string msg = null)
        {
            managedCertificate.DateLastRenewalAttempt = DateTime.UtcNow;

            if (status == RequestState.Success)
            {
                managedCertificate.RenewalFailureCount = 0;
                managedCertificate.LastRenewalStatus = RequestState.Success;
                managedCertificate.RenewalFailureMessage = "";
            }
            else if (status == RequestState.Paused)
            {
                managedCertificate.RenewalFailureCount = 0;
                managedCertificate.LastRenewalStatus = RequestState.Paused;
                managedCertificate.RenewalFailureMessage = msg;
            }
            else
            {
                managedCertificate.RenewalFailureMessage = msg;
                managedCertificate.RenewalFailureCount++;
                managedCertificate.LastRenewalStatus = RequestState.Error;
            }

            managedCertificate = await _itemManager.UpdatedManagedCertificate(managedCertificate);

            // report request state to status hub clients
            OnManagedCertificateUpdated?.Invoke(managedCertificate);

            //if reporting enabled, send report

            if (managedCertificate.RequestConfig?.EnableFailureNotifications == true)
            {
                await ReportManagedCertificateStatus(managedCertificate);
            }

            if (_tc != null)
            {
                _tc.TrackEvent("UpdateManagedCertificatesStatus_" + status.ToString());
            }
        }

        private async Task ReportManagedCertificateStatus(ManagedCertificate managedCertificate)
        {
            if (CoreAppSettings.Current.EnableStatusReporting)
            {
                if (_pluginManager != null && _pluginManager.DashboardClient != null)
                {
                    var report = new Models.Shared.RenewalStatusReport
                    {
                        InstanceId = CoreAppSettings.Current.InstanceId,
                        MachineName = Environment.MachineName,
                        PrimaryContactEmail = GetPrimaryContactEmail(),
                        ManagedSite = managedCertificate,
                        AppVersion = Util.GetAppVersion().ToString()
                    };
                    try
                    {
                        await _pluginManager.DashboardClient.ReportRenewalStatusAsync(report);
                    }
                    catch (Exception)
                    {
                        // failed to report status
                        LogMessage(managedCertificate.Id, "Failed to send renewal status report.",
                            LogItemType.GeneralWarning);
                    }
                }
            }
        }

        public async Task DeleteManagedCertificate(string id)
        {
            var site = await _itemManager.GetManagedCertificate(id);
            if (site != null)
            {
                await _itemManager.DeleteManagedCertificate(site);
            }
        }

        private ProcessStartInfo _httpChallengeProcessInfo;
        private Process _httpChallengeProcess;
        private string _httpChallengeControlKey = Guid.NewGuid().ToString();
        private string _httpChallengeCheckKey = "configcheck";
        private System.Net.Http.HttpClient _httpChallengeServerClient = new System.Net.Http.HttpClient();
        private int _httpChallengePort = 80;

        private async Task<bool> IsHttpChallengeProcessStarted()
        {
            if (_httpChallengeServerClient != null)
            {
                var testUrl = $"http://127.0.0.1:{_httpChallengePort}/.well-known/acme-challenge/{_httpChallengeCheckKey}";

                try
                {
                    var response = await _httpChallengeServerClient.GetAsync(testUrl);
                    if (response.IsSuccessStatusCode)
                    {
                        var status = await _httpChallengeServerClient.GetStringAsync(testUrl);

                        if (status == "OK")
                        {
                            return true;
                        }
                    }
                    return false;
                }
                catch
                {
                    return false;
                }
            }
            else
            {
                return false;
            }
        }

        private async Task<bool> StartHttpChallengeServer()
        {
            if (!await IsHttpChallengeProcessStarted())
            {
                var cliPath = $"{AppContext.BaseDirectory}certify.exe";
                _httpChallengeProcessInfo = new ProcessStartInfo(cliPath, $"httpchallenge keys={_httpChallengeControlKey},{_httpChallengeCheckKey}")
                {
                    RedirectStandardInput = true,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false,
                    CreateNoWindow = false,

                    WorkingDirectory = AppContext.BaseDirectory
                };

                try
                {
                    _httpChallengeProcess = new Process { StartInfo = _httpChallengeProcessInfo };
                    _httpChallengeProcess.Start();
                    await Task.Delay(1000);
                }
                catch (Exception)
                {
                    // failed to start process
                    _httpChallengeProcess = null;
                    return false;
                }

                if (_httpChallengeServerClient == null)
                {
                    _httpChallengeServerClient = new System.Net.Http.HttpClient();
                    _httpChallengeServerClient.DefaultRequestHeaders.Add("User-Agent", Util.GetUserAgent() + " CertifyManager");
                }

                return await IsHttpChallengeProcessStarted();
            }
            else
            {
                await StopHttpChallengeServer();
                return false;
            }
        }

        private async Task<bool> StopHttpChallengeServer()
        {
            if (_httpChallengeServerClient != null)
            {
                try
                {
                    var response = await _httpChallengeServerClient.GetAsync($"http://127.0.0.1:{_httpChallengePort}/.well-known/acme-challenge/{_httpChallengeControlKey}");
                    if (response.IsSuccessStatusCode)
                    {
                        return true;
                    }
                    else
                    {
                        try
                        {
                            _httpChallengeProcess.CloseMainWindow();
                        }
                        catch { }
                    }
                }
                catch
                {
                    return true;
                }
            }
            return true;
        }

        /// <summary>
        /// Perform set of test challenges and configuration checks to determine if site appears
        /// valid for certificate requests
        /// </summary>
        /// <param name="managedCertificate"> managed site to check </param>
        /// <param name="isPreviewMode"> 
        /// If true, perform full set of checks (DNS etc), if false performs minimal/basic checks
        /// </param>
        /// <returns>  </returns>
        public async Task<List<StatusMessage>> TestChallenge(ILog log, ManagedCertificate managedCertificate,
            bool isPreviewMode, IProgress<RequestProgressState> progress = null)
        {
            var results = new List<StatusMessage>();

            if (managedCertificate.RequestConfig.PerformAutoConfig && managedCertificate.GetChallengeConfig(null).ChallengeType == SupportedChallengeTypes.CHALLENGE_TYPE_HTTP)
            {
                var serverCheck = await _serverProvider.RunConfigurationDiagnostics(managedCertificate.ServerSiteId);
                results.AddRange(serverCheck.ConvertAll(x => new StatusMessage { IsOK = !x.HasError, HasWarning = x.HasWarning, Message = x.Description }));
            }

            if (managedCertificate.GetChallengeConfig(null).ChallengeType == SupportedChallengeTypes.CHALLENGE_TYPE_HTTP)
            {
                if (CoreAppSettings.Current.EnableHttpChallengeServer)
                {
                    _httpChallengeServerAvailable = await StartHttpChallengeServer();

                    if (_httpChallengeServerAvailable)
                    {
                        results.Add(new StatusMessage { IsOK = true, Message = "Http Challenge Server process available." });
                    }
                    else
                    {
                        results.Add(new StatusMessage { IsOK = true, HasWarning = true, Message = "Built-in Http Challenge Server process unavailable or could not start. Challenge responses will fall back to IIS." });
                    }
                }
            }

            results.AddRange(
            await _challengeDiagnostics.TestChallengeResponse(
                    log,
                    _serverProvider,
                    managedCertificate,
                    isPreviewMode,
                    CoreAppSettings.Current.EnableDNSValidationChecks, progress
                )
             );

            if (progress != null)
            {
                if (results.Any(r => r.IsOK == false))
                {
                    ReportProgress(progress,
                        new RequestProgressState(RequestState.Error, "One or more tests failed", managedCertificate,
                            isPreviewMode));
                }
                else
                {
                    ReportProgress(progress,
                        new RequestProgressState(RequestState.Success, "All Tests Completed OK", managedCertificate,
                            isPreviewMode));
                }
            }

            if (CoreAppSettings.Current.EnableHttpChallengeServer)
            {
                await StopHttpChallengeServer();
            }

            return results;
        }

        private async Task<bool> IsManagedCertificateRunning(string id, ICertifiedServer iis = null)
        {
            var managedCertificate = await _itemManager.GetManagedCertificate(id);
            if (managedCertificate != null)
            {
                if (iis == null)
                {
                    iis = _serverProvider;
                }

                try
                {
                    return await iis.IsSiteRunning(managedCertificate.GroupId);
                }
                catch
                {
                    // by default we assume the site is running
                    return true;
                }

            }
            else
            {
                //site not identified, assume it is running
                return true;
            }
        }

        public async Task<List<ActionStep>> GeneratePreview(ManagedCertificate item) => await new PreviewManager().GeneratePreview(item, _serverProvider, this);

        public async Task<List<DnsZone>> GetDnsProviderZones(string providerTypeId, string credentialsId)
        {
            var dnsHelper = new Core.Management.Challenges.DnsChallengeHelper();
            var result = await dnsHelper.GetDnsProvider(providerTypeId, credentialsId, null);

            if (result.Provider != null)
            {
                return await result.Provider.GetZones();
            }
            else
            {
                return new List<DnsZone>();
            }
        }

        public async Task<List<ActionStep>> PerformDeploymentTask(ILog log, string managedCertificateId, string taskId, bool isPreviewOnly, bool skipDeferredTasks)
        {
            var steps = new List<ActionStep>();

            var managedCert = await GetManagedCertificate(managedCertificateId);

            if (managedCert==null)
            {
                steps.Add(new ActionStep { HasError = true, Title = "Deployment", Description = "Managed certificate not found. Could not deploy." });
            }

            if (log == null)
            {
                log = ManagedCertificateLog.GetLogger(managedCert.Id, _loggingLevelSwitch);
            }

            // perform or preview each task
            var credentialsManager = new CredentialsManager();

            var deploymentTasks = new List<DeploymentTask>();

            var taskList = managedCert.DeploymentTasks?.Where(t => string.IsNullOrEmpty(taskId) || taskId == t.Id);

            foreach (var taskConfig in taskList)
            {
                // add task to execution list unless the task is deferred and we are currently skipping deferred tasks

                if (!taskConfig.IsDeferred || (taskConfig.IsDeferred && !skipDeferredTasks))
                {
                    try
                    {
                        var provider = DeploymentTaskProviderFactory.Create(taskConfig.TaskTypeId.ToLower());

                        Dictionary<string, string> credentials = null;

                        if (!string.IsNullOrEmpty(taskConfig.ChallengeCredentialKey))
                        {
                            credentials = await credentialsManager.GetUnlockedCredentialsDictionary(taskConfig.ChallengeCredentialKey);
                        }

                        var deploymentTask = new DeploymentTask(provider, taskConfig, credentials);

                        deploymentTasks.Add(deploymentTask);
                    }
                    catch (Exception exp)
                    {
                        steps.Add(new ActionStep { HasError = true, Title = "Deployment Task: " + taskConfig.TaskName, Description = "Cannot create task provider for deployment task: " + exp.ToString() });
                    }
                }
            }

            foreach (var task in deploymentTasks)
            {
                var result = await task.Execute(log, managedCert, isPreviewOnly: isPreviewOnly);

                steps.Add(new ActionStep { HasError = !result.IsSuccess, Description = result.Message });
            }

            return steps;
        }
    }
}
