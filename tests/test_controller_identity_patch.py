from pathlib import Path
import sys
import tempfile
import unittest

sys.path.insert(0, str(Path(__file__).resolve().parents[1] / 'scripts'))
from patch_controller_identity import apply


HELPERS = '''IL2CPP_EXTERN_C IL2CPP_METHOD_ATTR int32_t NativeControllerIdentity_DetectStyle_mAA (InputDevice_tUNITY* ___0_device, const RuntimeMethod* method)
{
    return -1;
}
IL2CPP_EXTERN_C IL2CPP_METHOD_ATTR int32_t NativeControllerIdentity_ResolveGamepadType_mBB (InputDevice_tGAME* ___0_device, const RuntimeMethod* method)
{
    return -1;
}
IL2CPP_EXTERN_C IL2CPP_METHOD_ATTR void NativeControllerIdentity_PrepareBindings_mCC (int32_t ___0_gamepadType, const RuntimeMethod* method)
{
}
'''
STYLE = '''IL2CPP_EXTERN_C IL2CPP_METHOD_ATTR int32_t NewUnityInputDevice_DetectDeviceStyle_mDD (InputDevice_tUNITY* ___0_unityDevice, const RuntimeMethod* method)
{
    // Original DualSenseGamepadHID and SwitchProControllerHID paths.
    OriginalDesktopBranches();
    return 0;
}
'''
GAME = '''IL2CPP_EXTERN_C IL2CPP_METHOD_ATTR void InputHandler_SetActiveGamepadType_mEE (InputHandler_tHANDLER* __this, InputDevice_tGAME* ___0_inputDevice, const RuntimeMethod* method)
{
	{
		InputHandler_set_ActiveGamepadAlias_mAlias(__this, 0, NULL);
		int32_t L_0 = __this->___gamepadState;
		if (!L_0) { goto IL_9999; }
	}
	{
		il2cpp_codegen_runtime_class_init_inline(Application_tAPP_il2cpp_TypeInfo_var);
		int32_t L_1;
		L_1 = Application_get_platform_mPlatform(NULL);
		OriginalStyleClassification_InputDevice_get_DeviceStyle_();
		OriginalNameFallback_String_ToLowerInvariant_();
	}
IL_1234:
	{
		int32_t L_39 = __this->___activeGamepadType;
		InputHandler_MapControllerButtons_mBindings(__this, L_39, NULL);
		OriginalUiBindings();
	}
IL_9999:
	{
		return;
	}
}
'''


class ControllerIdentityPatchTests(unittest.TestCase):
    def fixture(self, folder, game=GAME, same_file=False):
        cpp = Path(folder)
        (cpp / 'NativeCompatibility.cpp').write_text(HELPERS)
        (cpp / 'Assembly-CSharp.cpp').write_text(STYLE + (game if same_file else ''))
        if not same_file:
            (cpp / 'Assembly-CSharp__1.cpp').write_text(game)
        return cpp

    def test_two_boundaries_preserve_original_binding_body_and_detached_guard(self):
        for same_file in (True, False):
            with self.subTest(same_file=same_file), tempfile.TemporaryDirectory() as folder:
                cpp = self.fixture(folder, same_file=same_file)
                report = apply(cpp)
                style = (cpp / 'Assembly-CSharp.cpp').read_text()
                game = style if same_file else (cpp / 'Assembly-CSharp__1.cpp').read_text()
                self.assertLess(style.index('if (nativeStyle >= 0)'), style.index('OriginalDesktopBranches();'))
                start = game.index('void InputHandler_SetActiveGamepadType')
                self.assertLess(game.index('if (!L_0)', start), game.index('int32_t nativeType', start))
                self.assertLess(game.index('int32_t nativeType', start), game.index('OriginalNameFallback_', start))
                self.assertIn('goto IL_1234;', game)
                self.assertIn('OriginalUiBindings();', game)
                self.assertEqual(game.count('InputHandler_MapControllerButtons_mBindings(__this, L_39, NULL);'), 1)
                self.assertTrue(report['ios_name_fallback_bypassed'])
                self.assertTrue(report['face_button_positions_unchanged'])
                with self.assertRaises(AssertionError): apply(cpp)

    def test_late_boundary_or_abi_changes_fail_without_partial_writes(self):
        for broken in [GAME.replace('InputDevice_tGAME', 'InputDevice_tChanged'),
                       GAME.replace('OriginalNameFallback_String_ToLowerInvariant_();', ''),
                       GAME.replace('___activeGamepadType;', '___changedField;'),
                       GAME.replace('___gamepadState', '___changedState'), GAME + GAME]:
            with self.subTest(broken=broken), tempfile.TemporaryDirectory() as folder:
                cpp = self.fixture(folder, broken)
                before = {p: p.read_bytes() for p in cpp.glob('*.cpp')}
                with self.assertRaises(AssertionError): apply(cpp)
                self.assertEqual(before, {p: p.read_bytes() for p in cpp.glob('*.cpp')})


if __name__ == '__main__':
    unittest.main()
