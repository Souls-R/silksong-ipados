"""Check the game's derived menu and its inherited Unity pointer dispatcher."""
from pathlib import Path
import sys
import tempfile
import unittest

sys.path.insert(0, str(Path(__file__).resolve().parents[1] / 'scripts'))
from patch_mouse_ui import apply


def method(name, args, body='', result='void'):
    return f'IL2CPP_EXTERN_C IL2CPP_METHOD_ATTR {result} {name}_mABCD ({args}, const RuntimeMethod* method)\n{{\n{body}\n\treturn;\n}}\n'


class MouseUIRoutes(unittest.TestCase):
    def test_actual_menu_and_inherited_dispatch_share_provider(self):
        with tempfile.TemporaryDirectory() as folder:
            root = Path(folder)
            helpers = {
                'NativeMouseUI_Prepare': 'HollowKnightInputModule* module',
                'NativeMouseUI_ConsumeMouse': 'StandaloneInputModule* module, MouseButtonEventData* data',
                'NativeMouseUI_MouseButtonDown': 'int button',
                'NativeMouseUI_PointerPosition': '',
                'NativeMouseUI_MouseWakeAxis': 'String* axis',
                'NativeMouseUI_BindingAdded': 'PlayerAction* action, BindingSource* binding',
                'NativeBindingLabel_ShowPrompt': 'MappableKey* key',
                'NativeBindingLabel_Restore': 'MappableKey* key',
            }
            (root / 'NativeCompatibility.cpp').write_text(''.join(method(k, v) for k, v in helpers.items()))
            game = method('HollowKnightInputModule_Process', 'HollowKnightInputModule* __this')
            game += method('HollowKnightInputModule_UpdateModule', 'HollowKnightInputModule* __this', 'Input_get_mousePosition_m1234(NULL);')
            game += method('HollowKnightInputModule_ActivateModule', 'HollowKnightInputModule* __this', 'Input_get_mousePosition_m1234(NULL);\nInput_get_mousePosition_m1234(NULL);')
            game += method('HollowKnightInputModule_get_MouseButtonIsPressed', 'HollowKnightInputModule* __this', 'Input_GetMouseButtonDown_m1234(0, NULL);')
            game += method('InputHandler_Update', 'InputHandler* __this', 'Input_GetAxisRaw_m1234(axis, NULL);')
            for name in ['ListenForNewButton', 'ShowCurrentBinding', 'AbortRebind', 'OnDisable']:
                game += method('MappableKey_' + name, 'MappableKey* __this')
            game += method('MappableKey_OnBindingAdded', 'MappableKey* __this, PlayerAction* ___0_action, BindingSource* ___1_binding')
            unused = method('InControlInputModule_Process', 'InControlInputModule* __this')
            (root / 'Assembly-CSharp.cpp').write_text(game + unused)
            (root / 'UnityEngine.UI.cpp').write_text(method('StandaloneInputModule_ProcessMousePress', 'StandaloneInputModule* __this, MouseButtonEventData* ___0_data', 'OriginalClickDispatcher();'))
            report = apply(root)
            actual = (root / 'Assembly-CSharp.cpp').read_text()
            unity = (root / 'UnityEngine.UI.cpp').read_text()
            self.assertEqual(report['menu_module'], 'HollowKnightInputModule')
            self.assertIn(unused, actual)
            self.assertEqual(actual.count('NativeMouseUI_Prepare_mABCD(__this, NULL);'), 2)
            self.assertNotIn('Input_get_mousePosition_m1234(', actual)
            self.assertNotIn('Input_GetMouseButtonDown_m1234(', actual)
            self.assertIn('NativeMouseUI_ConsumeMouse_mABCD(__this, ___0_data, NULL)', unity)
            self.assertIn('OriginalClickDispatcher();', unity)
            self.assertIn('NativeMouseUI_BindingAdded_mABCD(___0_action, ___1_binding, NULL)', actual)


if __name__ == '__main__':
    unittest.main()
