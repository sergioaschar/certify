﻿using System.Collections.Generic;

namespace Certify.Models.Config
{
    public enum OptionType
    {
        String=1,
        MultiLineText=2,
        Boolean=3,
        Select=4,
        MultiSelect=5,
        RadioButton=6,
        Checkbox=7
    }

    public class ProviderParameter
    {
        public string Key { get; set; }
        public string Name { get; set; }
        public string Description { get; set; }
        public bool IsPassword { get; set; }
        public bool IsRequired { get; set; }
        public string Value { get; set; }
        public bool IsCredential { get; set; } = true;
        public string OptionsList { get; set; }
        public OptionType? Type { get; set; }

        public List<string> Options
        {
            get
            {
                var options = new List<string>();
                if (!string.IsNullOrEmpty(OptionsList))
                {
                    options.AddRange(OptionsList.Split(';'));
                }

                return options;
            }
        }
    }
}
