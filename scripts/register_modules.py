"""Inspect the stock engine's symbols before generating missing registration glue."""
from pathlib import Path
import subprocess, json, re
import argparse
parser=argparse.ArgumentParser(description=__doc__)
parser.add_argument('--project',type=Path,required=True)
parser.add_argument('--logs',type=Path,required=True)
args=parser.parse_args()
p=args.project/'Libraries'
result=subprocess.run(['xcrun','nm','-g',str(p/'libiPhone-lib.a')],capture_output=True,text=True,check=True)
(args.logs/'engine-symbols.txt').write_text(result.stdout)
# Definitions of these callbacks vary with stripping; only supply callbacks the
# archive leaves undefined. Module registration functions have C++ linkage.
defined=set(); undefined=set()
for line in result.stdout.splitlines():
    bits=line.split()
    if len(bits)>=2 and bits[-2]=='U': undefined.add(bits[-1])
    elif len(bits)>=3: defined.add(bits[-1])
missing=undefined-defined
(args.logs/'engine-undefined.json').write_text(json.dumps(sorted(missing),indent=2))
out=['// Generated from symbols of the matching official iOS engine archive.']
def callback(name, functions):
    mangled='__Z'+str(len(name))+name+'v'
    if mangled not in missing: return
    for f in functions: out.append('void '+f+'();')
    out.append('void '+name+'() {')
    out.extend('  '+f+'();' for f in functions)
    out.append('}')
modules=[]; calls=[]
for symbol in sorted(defined):
    m=re.fullmatch(r'__Z\d+(RegisterModuleClassesGenerated_\w+)v',symbol)
    if m: modules.append(m[1])
    m=re.fullmatch(r'__Z\d+(Register_\w+)v',symbol)
    if m: calls.append(m[1])
callback('RegisterAllClasses',['RegisterAllClassesGranular'])
callback('InvokeRegisterStaticallyLinkedModuleClasses',['RegisterStaticallyLinkedModuleClasses'])
if '_RegisterStaticallyLinkedModulesGranular' in missing:
    native_modules = sorted(s[1:] for s in defined if re.fullmatch(r'_RegisterModule_\w+',s))
    native_modules.sort(key=lambda s: (0 if s=='RegisterModule_SharedInternals' else 1 if s=='RegisterModule_Core' else 2,s))
    for f in native_modules: out.append('extern "C" void '+f+'();')
    out.append('extern "C" void RegisterStaticallyLinkedModulesGranular() {')
    out.extend('  '+f+'();' for f in native_modules)
    out.append('}')
callback('RegisterAllStrippedInternalCalls',calls)
(p/'RegisterModules.cpp').write_text('\n'.join(out)+'\n')
print('Registration candidates:',len(modules),len(calls),'generated lines:',len(out))
