// Copyright (c) 2026 YUCP Studio. VRCoplay contributors retain their copyrights.
// SPDX-License-Identifier: GPL-3.0-or-later
using System.Globalization;
using Microsoft.Windows.ApplicationModel.Resources;
using Microsoft.Windows.Globalization;
namespace VRCoplay;
internal static class AppStrings
{
    private static readonly Lazy<ResourceLoader> Loader = new(() => new());
    internal static string Get(string resourceId, string fallback)
    {
        var value = Loader.Value.GetString(resourceId.Replace('.', '/'));
        return string.IsNullOrEmpty(value) ? fallback : value;
    }
    internal static string Format(string resourceId, string fallback, params object?[] values) =>
        string.Format(CultureInfo.CurrentCulture, Get(resourceId, fallback), values);
    internal static void ApplyDisplayLanguage(int selection)
    {
        ApplicationLanguages.PrimaryLanguageOverride = selection switch
        {
            1 => "en-US",
            2 => "es-ES",
            3 => "fr-FR",
            _ => "",
        };
    }
}
