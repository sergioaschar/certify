﻿using System;
using Certify.Models;

namespace Certify.Management
{
    public sealed class CoreAppSettings
    {
        private static volatile CoreAppSettings instance;
        private static object syncRoot = new object();

        private CoreAppSettings()
        {
            // defaults
            SettingsSchemaVersion = 1;
            CheckForUpdatesAtStartup = true;
            EnableAppTelematics = true;
            IgnoreStoppedSites = true;
            EnableValidationProxyAPI = true;
            EnableAppTelematics = true;
            EnableEFS = false;
            EnableDNSValidationChecks = false;
            RenewalIntervalDays = 30;
            MaxRenewalRequests = 0;
            EnableHttpChallengeServer = true;
            LegacySettingsUpgraded = false;
            UseBackgroundServiceAutoRenewal = true;
            EnableCertificateCleanup = true;
            EnableStatusReporting = true;
            VaultPath = @"C:\ProgramData\ACMESharp";
            InstanceId = null;
        }

        public static CoreAppSettings Current
        {
            get
            {
                if (instance != null)
                {
                    return instance;
                }

                lock (syncRoot)
                {
                    if (instance == null)
                    {
                        instance = new CoreAppSettings();
                    }
                }

                return instance;
            }
            set
            {
                lock (syncRoot)
                {
                    instance = value;
                }
            }
        }

        public int SettingsSchemaVersion { get; set; }

        public bool CheckForUpdatesAtStartup { get; set; }

        public bool EnableAppTelematics { get; set; }

        public bool IgnoreStoppedSites { get; set; }

        public bool EnableValidationProxyAPI { get; set; }

        public bool EnableEFS { get; set; }

        public bool EnableDNSValidationChecks { get; set; }

        public int RenewalIntervalDays { get; set; }

        public int MaxRenewalRequests { get; set; }

        public bool EnableHttpChallengeServer { get; set; }

        public bool LegacySettingsUpgraded { get; set; }

        /// <summary>
        /// If true, this instance has been added to server dashboard
        /// </summary>
        public bool IsInstanceRegistered { get; set; }

        public string VaultPath { get; set; }

        /// <summary>
        /// If user opts for renewal failure reporting, generated instance id is used to group results
        /// </summary>
        public string InstanceId { get; set; }

        /// <summary>
        /// If set, specifies the UI language preference
        /// </summary>
        public string Language { get; set; }

        /// <summary>
        /// If true the background service will periodically perform auto renewals, otherwise auto
        /// renewal requires a scheduled task
        /// </summary>
        public bool UseBackgroundServiceAutoRenewal { get; set; }

        /// <summary>
        /// If true, daily task performs cleanup of expired certificates created by the app
        /// </summary>
        public bool EnableCertificateCleanup { get; set; }

        /// <summary>
        /// If true, app sends renewal status reports and other user prompts (manual dns steps etc)
        /// to the dashboard service
        /// </summary>
        public bool EnableStatusReporting { get; set; }

        public CertificateCleanupMode? CertificateCleanupMode { get; set; }
    }

    public class SettingsManager
    {
        private const string COREAPPSETTINGSFILE = "appsettings.json";

        public static bool FromPreferences(Models.Preferences prefs)
        {
            CoreAppSettings.Current.EnableAppTelematics = prefs.EnableAppTelematics;
            CoreAppSettings.Current.EnableDNSValidationChecks = prefs.EnableDNSValidationChecks;
            CoreAppSettings.Current.EnableValidationProxyAPI = prefs.EnableValidationProxyAPI;
            CoreAppSettings.Current.IgnoreStoppedSites = prefs.IgnoreStoppedSites;
            CoreAppSettings.Current.MaxRenewalRequests = prefs.MaxRenewalRequests;
            CoreAppSettings.Current.RenewalIntervalDays = prefs.RenewalIntervalDays;
            CoreAppSettings.Current.EnableEFS = prefs.EnableEFS;
            CoreAppSettings.Current.IsInstanceRegistered = prefs.IsInstanceRegistered;
            CoreAppSettings.Current.Language = prefs.Language;
            CoreAppSettings.Current.UseBackgroundServiceAutoRenewal = prefs.UseBackgroundServiceAutoRenewal;
            CoreAppSettings.Current.EnableHttpChallengeServer = prefs.EnableHttpChallengeServer;
            CoreAppSettings.Current.EnableCertificateCleanup = prefs.EnableCertificateCleanup;

            if (prefs.CertificateCleanupMode == null)
            {
                CoreAppSettings.Current.CertificateCleanupMode = CertificateCleanupMode.AfterExpiry;
            }
            else
            {
                CoreAppSettings.Current.CertificateCleanupMode = (CertificateCleanupMode)prefs.CertificateCleanupMode;
            }

            CoreAppSettings.Current.EnableStatusReporting = prefs.EnableStatusReporting;
            return true;
        }

        public static Models.Preferences ToPreferences()
        {
            LoadAppSettings();
            var prefs = new Models.Preferences
            {
                EnableAppTelematics = CoreAppSettings.Current.EnableAppTelematics,
                EnableDNSValidationChecks = CoreAppSettings.Current.EnableDNSValidationChecks,
                EnableValidationProxyAPI = CoreAppSettings.Current.EnableValidationProxyAPI,
                IgnoreStoppedSites = CoreAppSettings.Current.IgnoreStoppedSites,
                MaxRenewalRequests = CoreAppSettings.Current.MaxRenewalRequests,
                RenewalIntervalDays = CoreAppSettings.Current.RenewalIntervalDays,
                EnableEFS = CoreAppSettings.Current.EnableEFS,
                InstanceId = CoreAppSettings.Current.InstanceId,
                IsInstanceRegistered = CoreAppSettings.Current.IsInstanceRegistered,
                Language = CoreAppSettings.Current.Language,
                UseBackgroundServiceAutoRenewal = CoreAppSettings.Current.UseBackgroundServiceAutoRenewal,
                EnableHttpChallengeServer = CoreAppSettings.Current.EnableHttpChallengeServer,
                EnableCertificateCleanup = CoreAppSettings.Current.EnableCertificateCleanup,
                EnableStatusReporting = CoreAppSettings.Current.EnableStatusReporting,
                CertificateCleanupMode = CoreAppSettings.Current.CertificateCleanupMode
            };

            return prefs;
        }

        public static void SaveAppSettings()
        {
            var appDataPath = Util.GetAppDataFolder();
            var json = Newtonsoft.Json.JsonConvert.SerializeObject(CoreAppSettings.Current, Newtonsoft.Json.Formatting.Indented);

            lock (COREAPPSETTINGSFILE)
            {
                System.IO.File.WriteAllText(appDataPath + "\\" + COREAPPSETTINGSFILE, json);
            }
        }

        public static void LoadAppSettings()
        {
            var appDataPath = Util.GetAppDataFolder();
            var path = appDataPath + "\\" + COREAPPSETTINGSFILE;

            if (System.IO.File.Exists(path))
            {
                //ensure permissions

                //load content
                lock (COREAPPSETTINGSFILE)
                {
                    var configData = System.IO.File.ReadAllText(path);
                    CoreAppSettings.Current = Newtonsoft.Json.JsonConvert.DeserializeObject<CoreAppSettings>(configData);

                    // init new settings if not set
                    if (CoreAppSettings.Current.CertificateCleanupMode == null)
                    {
                        CoreAppSettings.Current.CertificateCleanupMode = CertificateCleanupMode.AfterExpiry;
                    }

                }
            }
            else
            {
                // no core app settings yet

                CoreAppSettings.Current.LegacySettingsUpgraded = true;
                CoreAppSettings.Current.IsInstanceRegistered = false;
                CoreAppSettings.Current.Language = null;
                CoreAppSettings.Current.CertificateCleanupMode = CertificateCleanupMode.AfterExpiry;

                CoreAppSettings.Current.InstanceId = Guid.NewGuid().ToString();
                SaveAppSettings();
            }

            // if instance id not yet set, create it now and save
            if (string.IsNullOrEmpty(CoreAppSettings.Current.InstanceId))
            {
                CoreAppSettings.Current.InstanceId = Guid.NewGuid().ToString();
                SaveAppSettings();
            }
        }
    }
}
