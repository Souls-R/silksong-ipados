// AccessTools and Traverse: reflection helpers, and the one part of Harmony
// that works here exactly as it does anywhere else.
//
// IL2CPP keeps full metadata -- this port strips nothing -- so looking a
// private field up by name and reading it is as valid on the phone as it is on
// a PC. Plugins use these constantly for things that have nothing to do with
// patching, and reimplementing them faithfully costs little.

using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;

namespace HarmonyLib
{
    public static class AccessTools
    {
        public static readonly BindingFlags all =
            BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance |
            BindingFlags.Static | BindingFlags.GetField | BindingFlags.SetField |
            BindingFlags.GetProperty | BindingFlags.SetProperty;

        public static readonly BindingFlags allDeclared = all | BindingFlags.DeclaredOnly;

        public static Type TypeByName(string name)
        {
            var type = Type.GetType(name, false);
            if (type != null) return type;
            foreach (var assembly in AppDomain.CurrentDomain.GetAssemblies())
            {
                type = assembly.GetType(name, false);
                if (type != null) return type;
            }
            return null;
        }

        public static Type Inner(Type type, string name)
        {
            return type == null ? null : type.GetNestedType(name, all);
        }

        public static FieldInfo Field(Type type, string name)
        {
            if (type == null || name == null) return null;
            for (var t = type; t != null; t = t.BaseType)
            {
                var field = t.GetField(name, allDeclared);
                if (field != null) return field;
            }
            return null;
        }

        public static FieldInfo DeclaredField(Type type, string name)
        {
            return type == null || name == null ? null : type.GetField(name, allDeclared);
        }

        public static PropertyInfo Property(Type type, string name)
        {
            if (type == null || name == null) return null;
            for (var t = type; t != null; t = t.BaseType)
            {
                var property = t.GetProperty(name, allDeclared);
                if (property != null) return property;
            }
            return null;
        }

        public static PropertyInfo DeclaredProperty(Type type, string name)
        {
            return type == null || name == null ? null : type.GetProperty(name, allDeclared);
        }

        public static MethodInfo PropertyGetter(Type type, string name)
        {
            var property = Property(type, name);
            return property == null ? null : property.GetGetMethod(true);
        }

        public static MethodInfo PropertySetter(Type type, string name)
        {
            var property = Property(type, name);
            return property == null ? null : property.GetSetMethod(true);
        }

        public static MethodInfo Method(Type type, string name, Type[] parameters = null, Type[] generics = null)
        {
            if (type == null || name == null) return null;
            MethodInfo found = null;
            for (var t = type; t != null && found == null; t = t.BaseType)
            {
                found = parameters == null
                    ? t.GetMethods(allDeclared).FirstOrDefault(m => m.Name == name)
                    : t.GetMethod(name, allDeclared, null, parameters, null);
            }
            if (found != null && generics != null) found = found.MakeGenericMethod(generics);
            return found;
        }

        public static MethodInfo Method(string typeColonName, Type[] parameters = null, Type[] generics = null)
        {
            var parts = (typeColonName ?? "").Split(':');
            if (parts.Length != 2) return null;
            return Method(TypeByName(parts[0]), parts[1], parameters, generics);
        }

        public static MethodInfo DeclaredMethod(Type type, string name, Type[] parameters = null, Type[] generics = null)
        {
            if (type == null || name == null) return null;
            var found = parameters == null
                ? type.GetMethods(allDeclared).FirstOrDefault(m => m.Name == name)
                : type.GetMethod(name, allDeclared, null, parameters, null);
            if (found != null && generics != null) found = found.MakeGenericMethod(generics);
            return found;
        }

        public static ConstructorInfo Constructor(Type type, Type[] parameters = null, bool searchForStatic = false)
        {
            if (type == null) return null;
            var flags = searchForStatic ? allDeclared & ~BindingFlags.Instance : allDeclared & ~BindingFlags.Static;
            return type.GetConstructor(flags, null, parameters ?? new Type[0], null);
        }

        public static ConstructorInfo DeclaredConstructor(Type type, Type[] parameters = null, bool searchForStatic = false)
        {
            return Constructor(type, parameters, searchForStatic);
        }

        public static List<MethodInfo> GetDeclaredMethods(Type type)
        {
            return type == null ? new List<MethodInfo>() : type.GetMethods(allDeclared).ToList();
        }

        public static List<FieldInfo> GetDeclaredFields(Type type)
        {
            return type == null ? new List<FieldInfo>() : type.GetFields(allDeclared).ToList();
        }

        public static List<PropertyInfo> GetDeclaredProperties(Type type)
        {
            return type == null ? new List<PropertyInfo>() : type.GetProperties(allDeclared).ToList();
        }

        public static List<ConstructorInfo> GetDeclaredConstructors(Type type, bool? searchForStatic = null)
        {
            return type == null ? new List<ConstructorInfo>() : type.GetConstructors(allDeclared).ToList();
        }

        public static List<string> GetFieldNames(Type type)
        {
            return GetDeclaredFields(type).Select(f => f.Name).ToList();
        }

        public static List<string> GetPropertyNames(Type type)
        {
            return GetDeclaredProperties(type).Select(p => p.Name).ToList();
        }

        public static object CreateInstance(Type type)
        {
            return type == null ? null : Activator.CreateInstance(type, true);
        }

        public static T CreateInstance<T>()
        {
            return (T)CreateInstance(typeof(T));
        }

        public static Type[] GetTypesFromAssembly(Assembly assembly)
        {
            try { return assembly.GetTypes(); }
            catch (ReflectionTypeLoadException e) { return e.Types.Where(t => t != null).ToArray(); }
        }

        public static List<Assembly> AllAssemblies()
        {
            return AppDomain.CurrentDomain.GetAssemblies().ToList();
        }

        public static IEnumerable<Type> AllTypes()
        {
            return AllAssemblies().SelectMany(GetTypesFromAssembly);
        }

        /// <summary>
        /// Whether a field or property of this type would be null-checked to
        /// nothing. Harmony exposes it; a handful of plugins use it.
        /// </summary>
        public static object GetDefaultValue(Type type)
        {
            if (type == null || type == typeof(void)) return null;
            return type.IsValueType ? Activator.CreateInstance(type) : null;
        }
    }

    /// <summary>
    /// Harmony's fluent reflection wrapper.
    ///
    /// The subset plugins actually use: walk to a field, property or method by
    /// name, then read, write or call it. The generic type-based entry points
    /// are here too because they cost nothing.
    /// </summary>
    public class Traverse
    {
        readonly object _root;
        readonly Type _type;
        readonly FieldInfo _field;
        readonly PropertyInfo _property;
        readonly MethodInfo _method;

        Traverse(object root, Type type, FieldInfo field, PropertyInfo property, MethodInfo method)
        {
            _root = root;
            _type = type;
            _field = field;
            _property = property;
            _method = method;
        }

        public Traverse(object root) : this(root, root == null ? null : root.GetType(), null, null, null) { }
        public Traverse(Type type) : this(null, type, null, null, null) { }

        public static Traverse Create(object root) { return new Traverse(root); }
        public static Traverse Create(Type type) { return new Traverse(type); }
        public static Traverse Create<T>() { return new Traverse(typeof(T)); }

        public Traverse Field(string name)
        {
            var owner = _type ?? (_root == null ? null : _root.GetType());
            var field = AccessTools.Field(owner, name);
            return new Traverse(Value(), field == null ? null : field.FieldType, field, null, null);
        }

        public Traverse<T> Field<T>(string name)
        {
            return new Traverse<T>(Field(name));
        }

        public Traverse Property(string name)
        {
            var owner = _type ?? (_root == null ? null : _root.GetType());
            var property = AccessTools.Property(owner, name);
            return new Traverse(Value(), property == null ? null : property.PropertyType, null, property, null);
        }

        public Traverse<T> Property<T>(string name)
        {
            return new Traverse<T>(Property(name));
        }

        public Traverse Method(string name, params object[] arguments)
        {
            var owner = _type ?? (_root == null ? null : _root.GetType());
            var types = arguments.Select(a => a == null ? typeof(object) : a.GetType()).ToArray();
            var method = AccessTools.Method(owner, name, types) ?? AccessTools.Method(owner, name);
            return new Traverse(Value(), null, null, null, method) { _arguments = arguments };
        }

        object[] _arguments = new object[0];

        public object GetValue()
        {
            if (_field != null) return _field.GetValue(_field.IsStatic ? null : _root);
            if (_property != null) return _property.GetValue(_property.GetGetMethod(true).IsStatic ? null : _root, null);
            if (_method != null) return _method.Invoke(_method.IsStatic ? null : _root, _arguments);
            return _root;
        }

        public T GetValue<T>()
        {
            var value = GetValue();
            return value == null ? default(T) : (T)value;
        }

        public Traverse SetValue(object value)
        {
            if (_field != null) _field.SetValue(_field.IsStatic ? null : _root, value);
            else if (_property != null) _property.SetValue(_property.GetSetMethod(true).IsStatic ? null : _root, value, null);
            return this;
        }

        object Value() { return _field == null && _property == null && _method == null ? _root : GetValue(); }

        public override string ToString()
        {
            var value = GetValue();
            return value == null ? "null" : value.ToString();
        }
    }

    public class Traverse<T>
    {
        readonly Traverse _traverse;

        public Traverse(Traverse traverse) { _traverse = traverse; }

        public T Value
        {
            get { return _traverse.GetValue<T>(); }
            set { _traverse.SetValue(value); }
        }
    }
}
