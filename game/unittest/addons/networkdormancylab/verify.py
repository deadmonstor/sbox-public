"""Validate saved scenes and compile project code against an existing engine build."""
import argparse
import json
from pathlib import Path
import subprocess
import uuid
from xml.sax.saxutils import escape


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument("--engine", type=Path, default=Path("D:/sbox-public"))
    args = parser.parse_args()
    root = Path(__file__).resolve().parent
    manifest = json.loads((root / "benchmark.json").read_text())
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

    scenes = list((root / "Assets/scenes").glob("*.scene"))
    assert len(scenes) == len(manifest["scenarios"]) == 10
    assert (root / "Assets" / project["Metadata"]["StartupScene"]).exists()
    for index, scenario in enumerate(manifest["scenarios"]):
        scene = json.loads((root / scenario["scene"]).read_text())
        check_guids(scene)
        components = [c for go in scene["GameObjects"] for c in go["Components"]]
        controllers = [c for c in components if c["__type"] == "DormancyLab.DormancyScenario"]
        assert len(controllers) == 1 and controllers[0]["Scenario"] == scenario["case"]
        expected_next = manifest["scenarios"][index + 1]["scene"].removeprefix("Assets/") if index + 1 < len(scenes) else ""
        assert controllers[0]["NextScene"] == expected_next
        assert controllers[0]["AutoAdvance"] is True
        for required in ("Sandbox.NetworkHelper", "Sandbox.CameraComponent", "Sandbox.ScreenPanel", "DormancyLab.DormancyHud"):
            assert any(c["__type"] == required for c in components), required
    for path in (root / "ProjectSettings").glob("*.config"):
        check_guids(json.loads(path.read_text()))
    print(f"Validated {len(scenes)} scenes and {len(seen)} unique scene/settings GUIDs.", flush=True)

    managed = args.engine.resolve() / "game/bin/managed"
    references = ["Sandbox.System", "Sandbox.Engine", "Sandbox.Filesystem", "Sandbox.Reflection", "Sandbox.Mounting", "Microsoft.AspNetCore.Components"]
    analyzers = ["Sandbox.CodeUpgrader", "Sandbox.Generator"]
    for name in references + analyzers:
        assert (managed / f"{name}.dll").exists(), f"Missing existing assembly: {name}"
    verification = root / ".verification"
    verification.mkdir(exist_ok=True)
    xml = ['<Project Sdk="Microsoft.NET.Sdk">', '<PropertyGroup><TargetFramework>net10.0</TargetFramework><LangVersion>14</LangVersion><AssemblyName>networkdormancylab</AssemblyName><ImplicitUsings>enable</ImplicitUsings><Nullable>disable</Nullable><EnableDefaultCompileItems>false</EnableDefaultCompileItems><SandboxHostMigration>true</SandboxHostMigration></PropertyGroup>', '<ItemGroup><Compile Include="../Code/*.cs" /><Using Include="Sandbox.Internal.GlobalGameNamespace" Static="true" /><CompilerVisibleProperty Include="SandboxHostMigration" />']
    for name in references:
        xml.append(f'<Reference Include="{name}"><HintPath>{escape(str(managed / (name + ".dll")))}</HintPath></Reference>')
    for name in analyzers:
        xml.append(f'<Analyzer Include="{escape(str(managed / (name + ".dll")))}" />')
    xml.append('</ItemGroup></Project>')
    csproj = verification / "Compile.csproj"
    csproj.write_text("\n".join(xml) + "\n")
    subprocess.run(["dotnet", "build", str(csproj), "--nologo", "-v", "minimal"], check=True)
    print("Local API compile passed. Editor sandbox compilation and multiplayer execution are separate pending checks.")


if __name__ == "__main__":
    main()
