using System;
using System.Linq;
using System.Reflection;

namespace TempProfileFixer
{
    internal static class AppVersion
    {
        public static string Current
        {
            get
            {
                AssemblyInformationalVersionAttribute attribute = Assembly.GetExecutingAssembly()
                    .GetCustomAttributes(typeof(AssemblyInformationalVersionAttribute), false)
                    .OfType<AssemblyInformationalVersionAttribute>()
                    .FirstOrDefault();
                return attribute == null || String.IsNullOrWhiteSpace(attribute.InformationalVersion)
                    ? Assembly.GetExecutingAssembly().GetName().Version.ToString()
                    : attribute.InformationalVersion;
            }
        }
    }
}
