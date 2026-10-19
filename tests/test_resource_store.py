"""Exercise the runtime C# store when a local .NET SDK is available."""
from pathlib import Path
import os
import re
import shutil
import subprocess
import tempfile
import unittest
from xml.sax.saxutils import quoteattr

from scripts.common import REPO


class RuntimeResourceStore(unittest.TestCase):
    def test_native_ps_touchpad_input_boundary(self):
        self.run_csharp(['src/runtime/NativeControllerInput.cs', 'tests/ControllerInputTests.cs'])

    def test_mouse_menu_and_real_finger_input(self):
        self.run_csharp(['src/runtime/NativeMouseUI.cs', 'src/runtime/NativeBindingPointerGate.cs', 'tests/MouseUITests.cs'])

    def test_binding_release_cannot_reopen_capture(self):
        self.run_csharp(['src/runtime/NativeBindingPointerGate.cs', 'tests/BindingPointerGateTests.cs'])

    def test_native_mouse_button_snapshots_and_lifecycle(self):
        self.run_csharp(['src/runtime/NativeMouseProvider.cs', 'tests/MouseProviderTests.cs'])

    def test_runtime_resource_compatibility_files_and_cache(self):
        self.run_csharp(['src/runtime/NativeResourceStore.cs', 'tests/ResourceStoreTests.cs'])

    def test_original_resource_request_before_bootstrap_coroutine(self):
        self.run_csharp(['src/runtime/NativeResourceStore.cs', 'src/runtime/NativeBootstrap.cs',
                         'tests/BootstrapStubs.cs', 'tests/BootstrapTests.cs'])

    def run_csharp(self, files):
        dotnet = shutil.which('dotnet')
        if not dotnet:
            self.skipTest('C# runtime tests require a local .NET SDK (8 or newer)')
        sdks = subprocess.check_output([dotnet, '--list-sdks'], text=True)
        majors = [int(v) for v in re.findall(r'^(\d+)\.', sdks, re.MULTILINE) if int(v) >= 8]
        if not majors:
            self.skipTest('C# runtime tests require a local .NET SDK (8 or newer)')
        framework = f'net{max(majors)}.0'
        with tempfile.TemporaryDirectory() as tmp:
            root = Path(tmp)
            sources = [REPO / name for name in files]
            includes = ''.join(f'<Compile Include={quoteattr(str(p))} />' for p in sources)
            (root / 'RuntimeChecks.csproj').write_text(
                '<Project Sdk="Microsoft.NET.Sdk"><PropertyGroup>'
                f'<OutputType>Exe</OutputType><TargetFramework>{framework}</TargetFramework>'
                '<EnableDefaultCompileItems>false</EnableDefaultCompileItems>'
                '<NuGetAudit>false</NuGetAudit></PropertyGroup>'
                f'<ItemGroup>{includes}</ItemGroup></Project>', encoding='utf-8')
            # Framework reference packs ship with the SDK; no network packages.
            (root / 'NuGet.Config').write_text(
                '<configuration><packageSources><clear /></packageSources></configuration>', encoding='utf-8')
            env = dict(os.environ, DOTNET_CLI_HOME=str(root), DOTNET_SKIP_FIRST_TIME_EXPERIENCE='1',
                       DOTNET_CLI_TELEMETRY_OPTOUT='1', DOTNET_CLI_UI_LANGUAGE='en',
                       DOTNET_GENERATE_ASPNET_CERTIFICATE='false')
            for command in ([dotnet, 'build', '--nologo', '-v:q'],
                            [dotnet, str(root / f'bin/Debug/{framework}/RuntimeChecks.dll')]):
                result = subprocess.run(command, cwd=root, env=env, encoding='utf-8', errors='replace',
                                        capture_output=True, timeout=120)
                self.assertEqual(result.returncode, 0, result.stdout + result.stderr)


if __name__ == '__main__':
    unittest.main()
