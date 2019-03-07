﻿using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;
using Certify.UI.Windows;

namespace Certify.UI.Controls.ManagedCertificate
{
    public class ListOption
    {
        public string Title { get; set; }
        public string Description { get; set; }
        public object Value { get; set; }
    }

    /// <summary>
    /// Interaction logic for Deployment.xaml 
    /// </summary>
    public partial class Deployment : UserControl
    {
        protected Certify.UI.ViewModel.ManagedCertificateViewModel ItemViewModel => UI.ViewModel.ManagedCertificateViewModel.Current;

        public Deployment()
        {
            InitializeComponent();

            DeploymentSiteOptions.ItemsSource = new List<ListOption>
            {
                  new ListOption{
                    Title ="Auto",
                    Value = Models.DeploymentOption.Auto,
                    Description="Automatic Deployment, Use Defaults"
                },
                new ListOption{
                    Title ="Single Site (selected in Domains tab)",
                    Value = Models.DeploymentOption.SingleSite,
                    Description="Only update bindings for the selected website."
                },
                 new ListOption{
                    Title ="All Sites",
                    Value = Models.DeploymentOption.AllSites,
                    Description="Update bindings for all sites, as applicable."
                },
                  new ListOption{
                    Title ="Certificate Store Only",
                    Value = Models.DeploymentOption.DeploymentStoreOnly,
                    Description="Only store the certificate, no deployment."
                },
                   new ListOption{
                    Title ="No Deployment",
                    Value = Models.DeploymentOption.NoDeployment,
                    Description="Certificate is not deployed."
                }
            };

            DeploymentBindingUpdates.ItemsSource = new List<ListOption> {
                new ListOption
                {
                    Title="Add or Update https bindings as required",
                    Value=Models.DeploymentBindingOption.AddOrUpdate,
                    Description="Existing https bindings will be updated with the new certificate, new bindings will be created as required."
                },
                  new ListOption
                {
                    Title=" Update existing https bindings only",
                    Value=Models.DeploymentBindingOption.UpdateOnly,
                    Description="Existing https bindings will be updated with the new certificate as required."
                }
            };


        }

        private void DeploymentSiteOptions_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            // if deployment mode changes, apply defaults for the mode
            ItemViewModel.SelectedItem?.RequestConfig?.ApplyDeploymentOptionDefaults();

            if (ItemViewModel.SelectedItem != null)
            {
                ItemViewModel.SelectedItem.DeploymentTasks = new System.Collections.ObjectModel.ObservableCollection<Config.DeploymentTaskConfig>();
                ItemViewModel.SelectedItem?.DeploymentTasks.Add(
                    new Config.DeploymentTaskConfig { TaskTypeId = "Certify.Providers.DeploymentTasks.CertificateExport", TaskName = "Example Task", Description="This is a example deployment task which does something magical.", IsDeferred = false, IsFatalOnError = false, RetriesAllowed = 0, RetryDelaySeconds = 0, Parameters = new Dictionary<string, string>() }
                );
            }

        }

        private void AddDeploymentTask_Click(object sender, System.Windows.RoutedEventArgs e)
        {
            var dialog = new EditDeploymentTask
            {
               Owner = Window.GetWindow(this)
            };
            dialog.Show();
        }

        private void EditDeploymentTask_Click(object sender, System.Windows.RoutedEventArgs e)
        {
            var dialog = new EditDeploymentTask
            {
                Owner = Window.GetWindow(this)
            };
            dialog.Show();
        }
    }
}

