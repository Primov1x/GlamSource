using System.Reflection;
using System.Runtime.CompilerServices;

[assembly: AssemblyVersion("1.0.75.0")]
[assembly: AssemblyFileVersion("1.0.75.0")]
// ponytail: GenerateAssemblyInfo=false (see GlamSource.csproj) means the csproj's
// <InternalsVisibleTo>GlamSource.Mock</InternalsVisibleTo> MSBuild property never actually turns
// into this attribute — it needs the SDK's own assembly-info generation, which is off. Was silently
// never working (WebUiPage stayed inaccessible from GlamSource.Mock) until GlamSource.Mock's local
// test server tried to use it.
[assembly: InternalsVisibleTo("GlamSource.Mock")]