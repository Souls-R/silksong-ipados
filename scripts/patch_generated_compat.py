"""Apply bounded native compatibility fixes; original game/JSON IL is unchanged."""
from pathlib import Path
import argparse, json, re
from patch_original_display import apply as apply_original_display
from patch_animator_compat import apply as apply_animator_compat

parser = argparse.ArgumentParser(description=__doc__)
parser.add_argument('--cpp',required=True,type=Path)
args = parser.parse_args()
matches = []
pattern = re.compile(r'IL2CPP_EXTERN_C IL2CPP_METHOD_ATTR void DefaultContractResolver_GetCallbackMethodsForType_[^(]+\([^\n]+\)\s*\n\{')
for path in args.cpp.glob('Newtonsoft.Json*.cpp'):
    source = path.read_text(encoding='utf-8')
    for match in pattern.finditer(source): matches.append((path,source,match))
assert len(matches) == 1
path,source,match = matches[0]
assert '#pragma clang optimize off' not in source
start = match.start()
end = source.index('\nIL2CPP_EXTERN_C IL2CPP_METHOD_ATTR',match.end())
prefix = '// Unity issue 9476: retain JSON callbacks while avoiding the affected optimized method.\n#ifdef __clang__\n#pragma clang optimize off\n#endif\n'
suffix = '\n#ifdef __clang__\n#pragma clang optimize on\n#endif\n'
output = source[:start] + prefix + source[start:end] + suffix + source[end:]
path.write_text(output,encoding='utf-8',newline='\n')
report = dict(file=path.name,method='DefaultContractResolver.GetCallbackMethodsForType',
              reference='https://issuetracker.unity.com/issues/9476',
              workaround='Disable clang optimization for this single generated method',runtime_verified=False)
controller_pattern = re.compile(r'IL2CPP_EXTERN_C IL2CPP_METHOD_ATTR void InputHandler_SetActiveGamepadType_[^(]+\([^\n]+\)\s*\n\{')
controllers = []
for controller_path in args.cpp.glob('Assembly-CSharp*.cpp'):
    controller_source = controller_path.read_text(encoding='utf-8')
    for controller_match in controller_pattern.finditer(controller_source):
        controllers.append((controller_path, controller_source, controller_match))
assert len(controllers) == 1, 'Expected one original controller-style selection method'
controller_path, controller_source, controller_match = controllers[0]
controller_end = controller_source.index('\nIL2CPP_EXTERN_C IL2CPP_METHOD_ATTR', controller_match.end())
controller_method = controller_source[controller_match.start():controller_end]
old_guard = 'if ((((int32_t)L_1) == ((int32_t)2)))'
new_guard = 'if ((((int32_t)L_1) == ((int32_t)2)) || (((int32_t)L_1) == ((int32_t)8)))'
assert controller_method.count(old_guard) == 1, 'Controller platform gate changed; inspect before patching'
assert re.search(r'L_1 = Application_get_platform_\w+\(NULL\);\s*' + re.escape(old_guard), controller_method)
assert 'InputDevice_get_DeviceStyle_' in controller_method
# RuntimePlatform.IPhonePlayer (8) uses the same real device-style classification
# as desktop. Otherwise the original fallback selects Xbox360 (Back/Start),
# while InControl reports XboxOne View/Menu, leaving just those two keys unbound.
# This changes only this method's platform whitelist, not Application.platform,
# device events, bindings, action handling, or any scene/gameplay logic.
controller_output = controller_source[:controller_match.start()] + controller_method.replace(old_guard, new_guard) + controller_source[controller_end:]
controller_path.write_text(controller_output, encoding='utf-8', newline='\n')
report['controller_platform'] = dict(file=controller_path.name, method='InputHandler.SetActiveGamepadType',
    change='Allow IPhonePlayer (8) through the original desktop device-style classification',
    original_game_il_unchanged=True, runtime_verified=False)
report['original_display_menu'] = apply_original_display(args.cpp)
report['animator_enable_boundary'] = apply_animator_compat(args.cpp)
(args.cpp.parent/'native-compatibility-workarounds.json').write_text(json.dumps(report,indent=2))
print(json.dumps(report,indent=2))
