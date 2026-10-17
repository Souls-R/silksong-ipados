"""Route iPad pointer events before Unity turns them into simulated touches."""
import plistlib
import re


def configure_mouse(project):
    path = project / 'Classes/UI/UnityView+iOS.mm'
    text = path.read_text(encoding='utf-8')
    marker = 'extern "C" NSSet* NativeMouse_FilterTouches(NSSet*, UIEvent*, UIView*, bool);'
    if marker not in text:
        text = text.replace('#import "UnityView.h"', '#import "UnityView.h"\n' + marker)
        for phase in ('Begin', 'Ended', 'Cancelled', 'Moved'):
            pattern = rf'\{{ UnitySendTouches{phase}\(touches, event\); \}}'
            replacement = ('{ NSSet* direct = NativeMouse_FilterTouches(touches, event, self, ' +
                           ('true' if phase == 'Cancelled' else 'false') +
                           f'); if (direct.count) UnitySendTouches{phase}(direct, event); }}')
            text, count = re.subn(pattern, lambda _: replacement, text)
            if count != 1:
                raise ValueError(f'Expected one Unity mouse/touch boundary: {phase}, got {count}')
        path.write_text(text, encoding='utf-8')
    info_path = project / 'Info.plist'
    info = plistlib.loads(info_path.read_bytes())
    info['UIApplicationSupportsIndirectInputEvents'] = True
    info_path.write_bytes(plistlib.dumps(info))
