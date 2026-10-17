"""Observe native Animator enable boundaries without altering original game IL."""
from patch_original_display import METHOD


def apply(cpp):
    helper = (cpp / 'NativeCompatibility.cpp').read_text('utf-8')
    candidates = [m for m in METHOD.finditer(helper)
                  if m[1].startswith('NativeAnimatorCompatibility_AfterEnabled_m')]
    assert len(candidates) == 1
    notify = candidates[0]
    declaration = notify[0].rstrip()[:-1].rstrip() + ';'
    matches = []
    for path in cpp.glob('UnityEngine.CoreModule*.cpp'):
        source = path.read_text('utf-8')
        for match in METHOD.finditer(source):
            if match[1].startswith('Behaviour_set_enabled_m'):
                matches.append((path, source, match))
    assert len(matches) == 1, 'Expected exactly one public Behaviour.enabled setter'
    path, source, match = matches[0]
    getters = [m for m in METHOD.finditer(source) if m[1].startswith('Behaviour_get_enabled_m')]
    assert len(getters) == 1
    end = source.index('\n}', match.end()) + 2
    body = source[match.start():end]
    assert body.count('\t\treturn;') == 1 and 'Behaviour_set_enabled_Injected_' in body
    # Use the marshalled native pointer after original null validation, just
    # before the native setter. Only actual disabled -> enabled edges notify.
    call = '\t\tBehaviour_set_enabled_Injected_'
    index = body.index(call)
    body = body[:index] + '\t\tconst bool nativeAnimatorEnableEdge = ___0_value && !' + getters[0][1] + '(__this, NULL);\n' + body[index:]
    body = body.replace('\t\treturn;', '\t\tif (nativeAnimatorEnableEdge) ' + notify[1] + '(__this, NULL);\n\t\treturn;')
    output = source[:match.start()] + body + source[end:]
    first = METHOD.search(output).start()
    output = output[:first] + '// Generic Animator re-enable compatibility; original native setter retained.\n' + declaration + '\n' + output[first:]
    path.write_text(output, 'utf-8', newline='\n')
    return dict(file=path.name, method='Behaviour.set_enabled', boundary='after false -> true, before original caller resumes',
                helper=notify[1], scene_or_name_filters=False, original_game_il_unchanged=True, runtime_verified=False)
