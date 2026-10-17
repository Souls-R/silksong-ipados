"""Enable Apple's native performance overlay before Unity loads Metal."""
import argparse
import json
import plistlib
from pathlib import Path

HUD_ENV = {
    "MTL_HUD_ENABLED": "1",
    "MTL_HUD_ALIGNMENT": "bottomright",
    "MTL_HUD_SCALE": "0.20",
    "MTL_HUD_ELEMENTS": "fps,frameinterval,gputime,frameintervalgraph",
    "MTL_HUD_LOGGING_ENABLED": "0",
}
MARKER = "    // Native Metal performance HUD: configure before UnityFramework loads."


def configure(project):
    main = project / "MainApp/main.mm"
    source = main.read_text(encoding="utf-8")
    entry = "int main(int argc, char* argv[])\n{\n"
    if MARKER not in source:
        if source.count(entry) != 1:
            raise ValueError("Expected exactly one Unity app entry point")
        source = "#include <stdlib.h>\n" + source
        settings = MARKER + "\n" + "\n".join(
            f'    setenv("{key}", "{value}", 0);' for key, value in HUD_ENV.items()
        ) + "\n"
        source = source.replace(entry, entry + settings, 1)
        main.write_text(source, encoding="utf-8")
    for key, value in HUD_ENV.items():
        if f'setenv("{key}", "{value}", 0);' not in source:
            raise ValueError("Unexpected existing HUD configuration")
    if source.index(MARKER) > source.index("        id ufw = UnityFrameworkLoad();"):
        raise ValueError("HUD must be configured before Unity initializes")
    info_path = project / "Info.plist"
    info = plistlib.loads(info_path.read_bytes())
    info["MetalHudEnabled"] = True
    info_path.write_bytes(plistlib.dumps(info))
    return {
        "provider": "Apple Metal Performance HUD",
        "environment_defaults": HUD_ENV,
        "before_unity_framework_load": True,
        "custom_per_frame_game_code": False,
        "performance_logging_default": False,
        "version": info["CFBundleShortVersionString"],
        "build": info["CFBundleVersion"],
        "device_verified": False,
    }


if __name__ == "__main__":
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--project", type=Path, required=True)
    parser.add_argument("--report", type=Path, required=True)
    args = parser.parse_args()
    report = configure(args.project)
    args.report.parent.mkdir(parents=True, exist_ok=True)
    args.report.write_text(json.dumps(report, indent=2), encoding="utf-8")
    print(json.dumps(report, indent=2))
