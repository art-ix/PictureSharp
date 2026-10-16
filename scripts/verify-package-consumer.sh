#!/usr/bin/env bash
# Exercise the actual local package with an isolated cache and source mapping.
set -euo pipefail
project_root="$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")/.." && pwd)"
smoke_root="$(mktemp -d)"
trap 'rm -rf -- "$smoke_root"' EXIT
version="$(tr -d '[:space:]' < "$project_root/VERSION")"
dotnet pack "$project_root/src/PictureSharp/PictureSharp.csproj" -c Release -o "$smoke_root/feed"
python3 -I - "$smoke_root/feed/PictureSharp.$version.nupkg" <<'PYZIP'
import sys, zipfile, xml.etree.ElementTree as ET
with zipfile.ZipFile(sys.argv[1]) as package:
    names = package.namelist()
    required = {"LICENSE", "THIRD-PARTY-NOTICES.txt", "README.md", "lib/net10.0/PictureSharp.dll", "lib/net10.0/PictureSharp.xml", "build/PictureSharp.targets"}
    assert required.issubset(names), "Package is missing required files"
    assert [name for name in names if name.endswith(".dll")] == ["lib/net10.0/PictureSharp.dll"]
    assert not any(name.startswith("runtimes/") for name in names), "Unexpected native/runtime assets"
    spec = ET.fromstring(package.read("PictureSharp.nuspec"))
    assert not any(node.tag.rsplit("}", 1)[-1] == "dependency" for node in spec.iter()), "Library must be BCL-only"
print("NuGet contents checked: assembly, XML docs, README, licenses, global-usings targets, no dependencies/native assets")
PYZIP
mkdir -p "$smoke_root/consumer"
cp "$project_root/samples/PictureSharp.AotSmoke/Program.cs" "$smoke_root/consumer/Program.cs"
cat > "$smoke_root/consumer/Consumer.csproj" <<EOF
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <OutputType>Exe</OutputType>
    <TargetFramework>net10.0</TargetFramework>
    <LangVersion>14.0</LangVersion>
    <Nullable>enable</Nullable>
    <ImplicitUsings>enable</ImplicitUsings>
    <TreatWarningsAsErrors>true</TreatWarningsAsErrors>
    <RestorePackagesPath>$smoke_root/cache</RestorePackagesPath>
    <PublishTrimmed>true</PublishTrimmed>
    <TrimMode>full</TrimMode>
  </PropertyGroup>
  <ItemGroup><PackageReference Include="PictureSharp" Version="$version" /></ItemGroup>
</Project>
EOF
cat > "$smoke_root/consumer/NuGet.Config" <<EOF
<configuration>
  <packageSources>
    <clear />
    <add key="local" value="$smoke_root/feed" />
    <add key="nuget" value="https://api.nuget.org/v3/index.json" />
  </packageSources>
  <packageSourceMapping>
    <packageSource key="local"><package pattern="PictureSharp" /><package pattern="PictureSharp.*" /></packageSource>
    <packageSource key="nuget"><package pattern="*" /></packageSource>
  </packageSourceMapping>
</configuration>
EOF
dotnet publish "$smoke_root/consumer/Consumer.csproj" -c Release -r linux-x64 --self-contained true -o "$smoke_root/trimmed"
"$smoke_root/trimmed/Consumer"
dotnet publish "$smoke_root/consumer/Consumer.csproj" -c Release -r linux-x64 --self-contained true -p:PublishAot=true -o "$smoke_root/aot"
"$smoke_root/aot/Consumer"

# The package adds `global using PictureSharp;` under ImplicitUsings: the smoke program must build without that directive,
# and must not once the consumer opts out.
mkdir -p "$smoke_root/implicit"
cp "$smoke_root/consumer/Consumer.csproj" "$smoke_root/consumer/NuGet.Config" "$smoke_root/implicit/"
grep -v '^using PictureSharp;$' "$project_root/samples/PictureSharp.AotSmoke/Program.cs" > "$smoke_root/implicit/Program.cs"
dotnet build "$smoke_root/implicit/Consumer.csproj" -c Release -o "$smoke_root/implicit-bin"
dotnet "$smoke_root/implicit-bin/Consumer.dll"
if dotnet build "$smoke_root/implicit/Consumer.csproj" -c Release -p:PictureSharpImplicitUsings=false -o "$smoke_root/opt-out-bin" > "$smoke_root/opt-out.log" 2>&1 \
    || ! grep -q "CS0246" "$smoke_root/opt-out.log"; then
    echo "PictureSharpImplicitUsings=false did not remove the global using" >&2
    exit 1
fi
if ! dotnet msbuild "$smoke_root/implicit/Consumer.csproj" -getItem:Using | grep -q '"Identity": "PictureSharp"'; then
    echo "The PictureSharp global using is missing" >&2
    exit 1
fi
for framework in UseWindowsForms UseWPF UseMaui; do
    usings="$(dotnet msbuild "$smoke_root/implicit/Consumer.csproj" -getItem:Using -p:$framework=true)"
    if grep -q '"Identity": "PictureSharp"' <<< "$usings"; then
        echo "$framework=true kept the PictureSharp global using" >&2
        exit 1
    fi
done
echo "Global usings checked: added with ImplicitUsings, removed by opt-out and for Windows Forms, WPF and MAUI"

# Adapter packages: separate packages that depend only on PictureSharp and their target API.
for adapter in Drawing Skia AspNetCore; do
    dotnet pack "$project_root/src/PictureSharp.$adapter/PictureSharp.$adapter.csproj" -c Release -o "$smoke_root/feed"
done
python3 -I - "$smoke_root/feed" "$version" <<'PYADAPTERS'
import sys, zipfile, xml.etree.ElementTree as ET
feed, version = sys.argv[1], sys.argv[2]
expected = {"Drawing": {"System.Drawing.Common"}, "Skia": {"SkiaSharp"}, "AspNetCore": set()}
for adapter, targets in expected.items():
    package_id = f"PictureSharp.{adapter}"
    with zipfile.ZipFile(f"{feed}/{package_id}.{version}.nupkg") as package:
        names = package.namelist()
        required = {"LICENSE", "THIRD-PARTY-NOTICES.txt", "README.md", f"lib/net10.0/{package_id}.dll", f"lib/net10.0/{package_id}.xml"}
        assert required.issubset(names), f"{package_id} is missing required files"
        assert [name for name in names if name.endswith(".dll")] == [f"lib/net10.0/{package_id}.dll"], f"{package_id} has extra assemblies"
        assert not any(name.startswith("runtimes/") for name in names), f"{package_id} has native assets"
        spec = ET.fromstring(package.read(f"{package_id}.nuspec"))
        deps = {node.get("id"): node.get("version") for node in spec.iter() if node.tag.rsplit("}", 1)[-1] == "dependency"}
        assert deps.get("PictureSharp") == version, f"{package_id} must depend on PictureSharp {version}: {deps}"
        assert set(deps) - {"PictureSharp"} == targets, f"{package_id} has unexpected dependencies: {deps}"
        frameworks = {node.get("name") for node in spec.iter() if node.tag.rsplit("}", 1)[-1] == "frameworkReference"}
        assert frameworks == ({"Microsoft.AspNetCore.App"} if adapter == "AspNetCore" else set()), f"{package_id}: {frameworks}"
print("Adapter packages checked: one assembly each, XML docs, README, licenses, dependencies on PictureSharp and the target API only")
PYADAPTERS

# The consumer runs the adapter smoke program from the local packages; System.Drawing is compiled and trimmed, and
# runs only on Windows.
mkdir -p "$smoke_root/adapters"
cp "$project_root/samples/PictureSharp.AdaptersSmoke/Program.cs" "$smoke_root/consumer/NuGet.Config" "$smoke_root/adapters/"
skia_native="$(sed -n 's/.*"SkiaSharp.NativeAssets.Linux" Version="\([^"]*\)".*/\1/p' "$project_root/samples/PictureSharp.AdaptersSmoke/PictureSharp.AdaptersSmoke.csproj")"
cat > "$smoke_root/adapters/Adapters.csproj" <<EOF
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <OutputType>Exe</OutputType>
    <TargetFramework>net10.0</TargetFramework>
    <LangVersion>14.0</LangVersion>
    <Nullable>enable</Nullable>
    <ImplicitUsings>enable</ImplicitUsings>
    <TreatWarningsAsErrors>true</TreatWarningsAsErrors>
    <RestorePackagesPath>$smoke_root/cache</RestorePackagesPath>
    <PublishTrimmed>true</PublishTrimmed>
    <TrimMode>full</TrimMode>
  </PropertyGroup>
  <ItemGroup>
    <PackageReference Include="PictureSharp.Drawing" Version="$version" />
    <PackageReference Include="PictureSharp.Skia" Version="$version" />
    <PackageReference Include="PictureSharp.AspNetCore" Version="$version" />
    <PackageReference Include="SkiaSharp.NativeAssets.Linux" Version="$skia_native" />
  </ItemGroup>
</Project>
EOF
dotnet publish "$smoke_root/adapters/Adapters.csproj" -c Release -r linux-x64 --self-contained true -o "$smoke_root/adapters-trimmed"
"$smoke_root/adapters-trimmed/Adapters"
dotnet publish "$smoke_root/adapters/Adapters.csproj" -c Release -r linux-x64 --self-contained true -p:PublishAot=true -o "$smoke_root/adapters-aot"
"$smoke_root/adapters-aot/Adapters"
echo "Adapter consumer checked: local packages, full trimming and Native AOT linux-x64"
