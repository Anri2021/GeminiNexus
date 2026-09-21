"""Compile current contracts, provider and Razor sources using SDK references and restored Polly.Core.
Complements, never replaces, the full package-aware build and integration tests.
"""
import argparse, pathlib, subprocess, tempfile, html
ROOT=pathlib.Path(__file__).resolve().parents[1]
p=argparse.ArgumentParser();p.add_argument('--dotnet',default='dotnet');args=p.parse_args()
with tempfile.TemporaryDirectory() as directory:
    target=pathlib.Path(directory)
    sources=['GeminiNexus.Shared/WorkspaceContracts.cs','GeminiNexus.Shared/PluginEngine.cs','GeminiNexus.Shared/ComputeKernels.cs','GeminiNexus.UI/Services/*.cs','GeminiNexus.Server/Providers/*.cs','GeminiNexus.Server/Application/ServerOptions.cs','GeminiNexus.Server/Application/TraceRedactor.cs','GeminiNexus.Server/Domain/*.cs','tests/RuntimeChecks.cs']
    razor=['Pages/Workspace.razor','Components/MessageBody.razor','Components/MediaParts.razor','_Imports.razor']
    items=''.join(f'<Compile Include="{html.escape(str(ROOT/s))}" />' for s in sources)
    items+=''.join(f'<RazorComponent Include="{html.escape(str(ROOT/"GeminiNexus.UI"/s))}" Link="{s}" />' for s in razor)
    polly=pathlib.Path.home()/'.nuget/packages/polly.core/8.8.0/lib/net8.0/Polly.Core.dll'
    if not polly.exists():raise RuntimeError('Polly.Core must be restored by the full build before source checks')
    (target/'Checks.csproj').write_text('<Project Sdk="Microsoft.NET.Sdk.Razor"><PropertyGroup><TargetFramework>net11.0</TargetFramework><OutputType>Exe</OutputType><LangVersion>15.0</LangVersion><Nullable>enable</Nullable><ImplicitUsings>enable</ImplicitUsings><AllowUnsafeBlocks>true</AllowUnsafeBlocks><RootNamespace>GeminiNexus.UI</RootNamespace><JsonSerializerIsReflectionEnabledByDefault>false</JsonSerializerIsReflectionEnabledByDefault></PropertyGroup><ItemGroup><FrameworkReference Include="Microsoft.AspNetCore.App"/><Reference Include="Polly.Core" HintPath="'+html.escape(str(polly))+'"/><Using Include="Microsoft.Extensions.Configuration"/><Using Include="Microsoft.Extensions.Logging"/>'+items+'</ItemGroup></Project>')
    (target/'NuGet.Config').write_text('<configuration><packageSources><clear/></packageSources></configuration>')
    subprocess.run([args.dotnet,'run','--project',str(target/'Checks.csproj'),'-p:UseSharedCompilation=false','--',str(ROOT)],check=True)
