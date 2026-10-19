gem 'xcodeproj', '=1.27.0'
require 'xcodeproj'
root = File.expand_path(ARGV.fetch(0))
bundle_id = ARGV.fetch(1)
p = Xcodeproj::Project.open(File.join(root, 'Unity-iPhone.xcodeproj'))
framework = p.targets.find { |t| t.name == 'UnityFramework' }
app = p.targets.find { |t| t.name == 'Unity-iPhone' }
game = p.targets.find { |t| t.name == 'GameAssembly' }
if game
  framework.dependencies.select { |d| d.target == game }.each(&:remove_from_project)
  game.remove_from_project
end
p.targets.select { |t| t.name.include?('Tests') }.each(&:remove_from_project)
p.files.select { |f| f.path == 'libGameAssembly.a' }.each do |f|
  f.path = 'Libraries/libGameAssembly.a'
  f.source_tree = 'SOURCE_ROOT'
end
p.build_configurations.each do |c|
  c.build_settings['PRODUCT_NAME_APP'] = 'Unity-iPhone'
  c.build_settings['IPHONEOS_DEPLOYMENT_TARGET'] = '16.0'
  c.build_settings['ENABLE_BITCODE'] = 'NO'
end
p.targets.each do |t|
  t.build_configurations.each do |c|
    b = c.build_settings
    b['CODE_SIGNING_ALLOWED'] = 'NO'
    b['CODE_SIGNING_REQUIRED'] = 'NO'
    b['IPHONEOS_DEPLOYMENT_TARGET'] = '16.0'
    b['ARCHS'] = 'arm64'
    b['ENABLE_USER_SCRIPT_SANDBOXING'] = 'NO'
    b['GCC_PREPROCESSOR_DEFINITIONS'] = ['$(inherited)','UNITY_VERSION_VER=6000','UNITY_VERSION_MAJ=0','UNITY_VERSION_MIN=50']
    b['CLANG_CXX_LANGUAGE_STANDARD'] = 'c++17'
    b['CLANG_CXX_LIBRARY'] = 'libc++'
    b['HEADER_SEARCH_PATHS'] = ['$(inherited)','$(SRCROOT)/Classes','$(SRCROOT)/Classes/Unity','$(SRCROOT)/Libraries','$(SRCROOT)/Il2CppOutput','$(SRCROOT)/../IL2CPP/libil2cpp','$(SRCROOT)/../IL2CPP/libil2cpp/pch']
    b['HEADER_SEARCH_PATHS'] += ['$(SRCROOT)/../IL2CPP/external/baselib/Include','$(SRCROOT)/../IL2CPP/external/baselib/Platforms/IOS/Include']
    b['GCC_PREPROCESSOR_DEFINITIONS'] += ['BASELIB_INLINE_NAMESPACE=il2cpp_baselib']
    b['PRODUCT_BUNDLE_IDENTIFIER'] = t == app ? bundle_id : 'com.unity3d.framework'
    b['PRODUCT_NAME'] = 'Unity-iPhone' if t == app
    b['ASSETCATALOG_COMPILER_APPICON_NAME'] = t == app ? 'AppIcon' : ''
  end
end
app.product_reference.path = 'Unity-iPhone.app'
# Remove the Editor-generated icon catalog reference, absent in this manual build.
p.files.select { |f| f.path.to_s.end_with?('Images.xcassets') }.each do |f|
  f.referrers.grep(Xcodeproj::Project::Object::PBXBuildFile).each(&:remove_from_project)
  f.remove_from_project
end
# Data is a folder reference to preserve Unity's internal paths.
data = p.files.find { |f| f.path == 'Data' } || p.main_group.new_file('Data', 'SOURCE_ROOT')
data.last_known_file_type = 'folder'
app.resources_build_phase.add_file_reference(data) unless app.resources_build_phase.files_references.include?(data)
%w[Metal MetalKit GameController CoreHaptics Security WebKit SafariServices Accelerate CoreText].each do |name|
  unless framework.frameworks_build_phase.files_references.any? { |f| f.path.to_s.end_with?("#{name}.framework") }
    framework.add_system_framework(name)
  end
end
reg = p.files.find { |f| f.path == 'Libraries/RegisterModules.cpp' } || p.main_group.new_file('Libraries/RegisterModules.cpp', 'SOURCE_ROOT')
framework.source_build_phase.add_file_reference(reg) unless framework.source_build_phase.files_references.include?(reg)
%w[NativeKeyboard.mm NativeMouse.mm NativeControllerInput.mm NativeLaunch.mm NativeHaptics.mm].each do |name|
  if File.exist?(File.join(root, 'Libraries', name))
    path = 'Libraries/' + name
    ref = p.files.find { |f| f.path == path } || p.main_group.new_file(path, 'SOURCE_ROOT')
    build = framework.source_build_phase.files.find { |f| f.file_ref == ref } || framework.source_build_phase.add_file_reference(ref)
    build.settings = {'COMPILER_FLAGS' => '-fobjc-arc'} if ['NativeLaunch.mm', 'NativeHaptics.mm', 'NativeMouse.mm', 'NativeControllerInput.mm'].include?(name)
  end
end
%w[NativeIcons.xcassets Settings.bundle].each do |path|
  ref = p.files.find { |f| f.path == path } || p.main_group.new_file(path, 'SOURCE_ROOT')
  ref.last_known_file_type = path.end_with?('.xcassets') ? 'folder.assetcatalog' : 'wrapper.plug-in'
  app.resources_build_phase.add_file_reference(ref) unless app.resources_build_phase.files_references.include?(ref)
end
group = p.main_group.children.find { |g| g.isa == 'PBXVariantGroup' && g.name == 'InfoPlist.strings' } || p.main_group.new_variant_group('InfoPlist.strings')
%w[en zh-Hans zh-Hant].each do |language|
  path = language + '.lproj/InfoPlist.strings'
  ref = group.children.find { |f| f.path == path } || group.new_file(path, 'SOURCE_ROOT')
  ref.name = language
  p.root_object.known_regions << language unless p.root_object.known_regions.include?(language)
end
app.resources_build_phase.add_file_reference(group) unless app.resources_build_phase.files_references.include?(group)
Dir.glob(File.join(root, 'Libraries/*.a')).each do |path|
  name = File.basename(path)
  next if ['libiPhone-lib.a','libGameAssembly.a'].include?(name)
  ref = p.files.find { |f| f.path == 'Libraries/' + name } || p.main_group.new_file('Libraries/' + name, 'SOURCE_ROOT')
  framework.frameworks_build_phase.add_file_reference(ref) unless framework.frameworks_build_phase.files_references.include?(ref)
end
p.save
