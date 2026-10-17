using System.Globalization;
using System.Resources;

namespace Dependency;
public static class Identity
{
#if POISON
    public static string Managed => "poison-managed";
#else
    public static string Managed => "verified-managed";
#endif
    public static string Resource => new ResourceManager("Dependency.LoaderValues", typeof(Identity).Assembly)
        .GetString("value", CultureInfo.GetCultureInfo("fr"))!;
}
