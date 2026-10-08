"""Validate saved scenes and compile only this addon against existing engine assemblies."""
import argparse
import json
from pathlib import Path
import subprocess
import uuid
from xml.sax.saxutils import escape


def main():
    root = Path(__file__).resolve().parent
    parser = argparse.ArgumentParser()
    parser.add_argument("--engine", type=Path, default=root.parents[3])
    args = parser.parse_args()
    manifest = json.loads((root / "workloads.json").read_text())
    project = json.loads((root / ".sbproj").read_text())
    seen = set()

    def check_guids(value):
        if isinstance(value, dict):
            if "__guid" in value:
                guid = str(uuid.UUID(value["__guid"]))
                assert guid not in seen, f"Duplicate GUID: {guid}"
                seen.add(guid)
            for child in value.values():
                check_guids(child)
        elif isinstance(value, list):
            for child in value:
                check_guids(child)

    assert (root / "Assets" / project["Metadata"]["StartupScene"]).exists()
    assert len(list((root / "Assets/scenes").glob("*.scene"))) == len(manifest["scenarios"])
    for scenario in manifest["scenarios"]:
        scene = json.loads((root / scenario["scene"]).read_text())
        check_guids(scene)
        components = [c for go in scene["GameObjects"] for c in go["Components"]]
        controllers = [c for c in components if c["__type"] == "NetworkObjectLoad.NetworkObjectScenario"]
        assert len(controllers) == 1 and controllers[0]["Scenario"] == scenario["case"]
        for kind in ("Sandbox.NetworkHelper", "Sandbox.CameraComponent", "Sandbox.ScreenPanel", "NetworkObjectLoad.NetworkLoadHud"):
            assert any(c["__type"] == kind for c in components), kind
        assert scene["SceneProperties"]["FixedUpdateFrequency"] == 50
    for path in (root / "ProjectSettings").glob("*.config"):
        check_guids(json.loads(path.read_text()))
    print(f"Validated {len(manifest['scenarios'])} scenes and {len(seen)} unique scene/settings GUIDs.", flush=True)

    managed = args.engine.resolve() / "game/bin/managed"
    references = ["Sandbox.System", "Sandbox.Engine", "Sandbox.Filesystem", "Sandbox.Reflection", "Sandbox.Mounting", "Microsoft.AspNetCore.Components"]
    analyzers = ["Sandbox.CodeUpgrader", "Sandbox.Generator"]
    for name in references + analyzers:
        assert (managed / f"{name}.dll").exists(), f"Missing existing assembly: {name}"
    verification = root / ".verification"
    verification.mkdir(exist_ok=True)
    xml = ['<Project Sdk="Microsoft.NET.Sdk">', '<PropertyGroup><TargetFramework>net10.0</TargetFramework><LangVersion>14</LangVersion><AssemblyName>networkobjectload</AssemblyName><ImplicitUsings>enable</ImplicitUsings><Nullable>disable</Nullable><EnableDefaultCompileItems>false</EnableDefaultCompileItems><SandboxHostMigration>true</SandboxHostMigration></PropertyGroup>', '<ItemGroup><Compile Include="../code/*.cs" /><Using Include="Sandbox.Internal.GlobalGameNamespace" Static="true" /><CompilerVisibleProperty Include="SandboxHostMigration" />']
    for name in references:
        xml.append(f'<Reference Include="{name}"><HintPath>{escape(str(managed / (name + ".dll")))}</HintPath></Reference>')
    for name in analyzers:
        xml.append(f'<Analyzer Include="{escape(str(managed / (name + ".dll")))}" />')
    xml.append('</ItemGroup></Project>')
    csproj = verification / "Compile.csproj"
    csproj.write_text("\n".join(xml) + "\n")
    subprocess.run(["dotnet", "build", str(csproj), "--nologo", "-v", "minimal"], check=True)
    print("Local API compile passed. Editor sandbox compilation and multiplayer execution remain separate checks.")


if __name__ == "__main__":
    main()
