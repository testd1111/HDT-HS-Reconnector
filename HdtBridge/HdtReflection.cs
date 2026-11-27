using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Reflection;
using System.Windows.Threading;

namespace HdtBridge
{
    internal static class HdtReflection
    {
        public const BindingFlags AnyInstance = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance;
        public const BindingFlags AnyStatic = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static;

        private sealed class MemberAccessor
        {
            public PropertyInfo Property;
            public FieldInfo Field;
            public Type ValueType => Property != null ? Property.PropertyType : Field?.FieldType;
            public bool Exists => Property != null || Field != null;
        }

        private struct MemberKey : IEquatable<MemberKey>
        {
            public readonly Type Owner;
            public readonly string Name;
            public readonly bool IsStatic;

            public MemberKey(Type owner, string name, bool isStatic)
            {
                Owner = owner;
                Name = name;
                IsStatic = isStatic;
            }

            public bool Equals(MemberKey other)
            {
                return ReferenceEquals(Owner, other.Owner)
                    && IsStatic == other.IsStatic
                    && string.Equals(Name, other.Name, StringComparison.Ordinal);
            }

            public override bool Equals(object obj) => obj is MemberKey other && Equals(other);

            public override int GetHashCode()
            {
                unchecked
                {
                    var h = Owner != null ? Owner.GetHashCode() : 0;
                    h = (h * 397) ^ (Name != null ? StringComparer.Ordinal.GetHashCode(Name) : 0);
                    return (h * 397) ^ (IsStatic ? 1 : 0);
                }
            }
        }

        private struct MethodKey : IEquatable<MethodKey>
        {
            public readonly Type Owner;
            public readonly string Name;
            public readonly bool IsStatic;
            public readonly int ArgCount;
            public readonly Type Arg0;
            public readonly Type Arg1;
            public readonly Type Arg2;

            public MethodKey(Type owner, string name, bool isStatic, Type[] argTypes)
            {
                Owner = owner;
                Name = name;
                IsStatic = isStatic;
                ArgCount = argTypes?.Length ?? 0;
                Arg0 = ArgCount > 0 ? argTypes[0] : null;
                Arg1 = ArgCount > 1 ? argTypes[1] : null;
                Arg2 = ArgCount > 2 ? argTypes[2] : null;
            }

            public bool Equals(MethodKey other)
            {
                return ReferenceEquals(Owner, other.Owner)
                    && IsStatic == other.IsStatic
                    && ArgCount == other.ArgCount
                    && ReferenceEquals(Arg0, other.Arg0)
                    && ReferenceEquals(Arg1, other.Arg1)
                    && ReferenceEquals(Arg2, other.Arg2)
                    && string.Equals(Name, other.Name, StringComparison.Ordinal);
            }

            public override bool Equals(object obj) => obj is MethodKey other && Equals(other);

            public override int GetHashCode()
            {
                unchecked
                {
                    var h = Owner != null ? Owner.GetHashCode() : 0;
                    h = (h * 397) ^ (Name != null ? StringComparer.Ordinal.GetHashCode(Name) : 0);
                    h = (h * 397) ^ (IsStatic ? 1 : 0);
                    h = (h * 397) ^ ArgCount;
                    h = (h * 397) ^ (Arg0 != null ? Arg0.GetHashCode() : 0);
                    h = (h * 397) ^ (Arg1 != null ? Arg1.GetHashCode() : 0);
                    h = (h * 397) ^ (Arg2 != null ? Arg2.GetHashCode() : 0);
                    return h;
                }
            }
        }

        private static readonly object CacheLock = new object();
        private static readonly Dictionary<string, Type> TypeCache = new Dictionary<string, Type>(StringComparer.Ordinal);
        private static readonly Dictionary<string, object> GameTagCache = new Dictionary<string, object>(StringComparer.Ordinal);
        private static readonly ConcurrentDictionary<MemberKey, MemberAccessor> MemberCache = new ConcurrentDictionary<MemberKey, MemberAccessor>();
        private static readonly ConcurrentDictionary<MethodKey, MethodInfo> MethodCache = new ConcurrentDictionary<MethodKey, MethodInfo>();
        private static Assembly cachedHdtAssembly;

        public static Assembly FindHdtAssembly()
        {
            lock (CacheLock)
            {
                if (cachedHdtAssembly != null)
                    return cachedHdtAssembly;

                foreach (var assembly in AppDomain.CurrentDomain.GetAssemblies())
                {
                    try
                    {
                        var name = assembly.GetName().Name;
                        if (string.Equals(name, "HearthstoneDeckTracker", StringComparison.OrdinalIgnoreCase)
                            || assembly.GetType("Hearthstone_Deck_Tracker.Core", false) != null)
                        {
                            cachedHdtAssembly = assembly;
                            return assembly;
                        }
                    }
                    catch
                    {
                        // Ignore dynamic or inaccessible assemblies.
                    }
                }
            }

            return null;
        }

        public static Type FindType(string fullName)
        {
            if (string.IsNullOrEmpty(fullName))
                return null;

            lock (CacheLock)
            {
                if (TypeCache.TryGetValue(fullName, out var cached))
                    return cached;
            }

            Type resolved = null;
            var hdtAsm = FindHdtAssembly();
            if (hdtAsm != null)
                resolved = hdtAsm.GetType(fullName, false);

            if (resolved == null)
            {
                foreach (var assembly in AppDomain.CurrentDomain.GetAssemblies())
                {
                    try
                    {
                        resolved = assembly.GetType(fullName, false);
                        if (resolved != null)
                            break;
                    }
                    catch
                    {
                        // Ignore inaccessible assemblies.
                    }
                }
            }

            if (resolved != null)
            {
                lock (CacheLock)
                    TypeCache[fullName] = resolved;
            }

            return resolved;
        }

        public static bool HasMember(Type type, string name, bool isStatic)
        {
            if (type == null || string.IsNullOrEmpty(name))
                return false;

            return GetMemberAccessor(type, name, isStatic).Exists;
        }

        public static bool HasMethod(Type type, string name, bool isStatic, int? parameterCount = null)
        {
            if (type == null || string.IsNullOrEmpty(name))
                return false;

            var flags = isStatic ? AnyStatic : AnyInstance;
            foreach (var method in type.GetMethods(flags))
            {
                if (!string.Equals(method.Name, name, StringComparison.Ordinal))
                    continue;
                if (!parameterCount.HasValue || method.GetParameters().Length == parameterCount.Value)
                    return true;
            }

            return false;
        }

        public static Type GetMemberType(Type ownerType, string name, bool isStatic)
        {
            if (ownerType == null || string.IsNullOrEmpty(name))
                return null;

            return GetMemberAccessor(ownerType, name, isStatic).ValueType;
        }

        public static object GetMemberValue(object instance, string name)
        {
            if (instance == null || string.IsNullOrEmpty(name))
                return null;

            var accessor = GetMemberAccessor(instance.GetType(), name, isStatic: false);
            if (accessor.Property != null)
                return accessor.Property.GetValue(instance, null);
            return accessor.Field?.GetValue(instance);
        }

        public static bool SetMemberValue(object instance, string name, object value)
        {
            if (instance == null || string.IsNullOrEmpty(name))
                return false;

            var accessor = GetMemberAccessor(instance.GetType(), name, isStatic: false);
            if (accessor.Property != null && accessor.Property.CanWrite)
            {
                accessor.Property.SetValue(instance, value, null);
                return true;
            }

            if (accessor.Field != null)
            {
                accessor.Field.SetValue(instance, value);
                return true;
            }

            return false;
        }

        public static object GetStaticMemberValue(Type type, string name)
        {
            if (type == null || string.IsNullOrEmpty(name))
                return null;

            var accessor = GetMemberAccessor(type, name, isStatic: true);
            if (accessor.Property != null)
                return accessor.Property.GetValue(null, null);
            return accessor.Field?.GetValue(null);
        }

        public static bool TryGetStaticFieldValue(Type type, string fieldName, out object value)
        {
            value = null;
            if (type == null || string.IsNullOrEmpty(fieldName))
                return false;

            var field = GetFieldHierarchy(type, fieldName, AnyStatic);
            if (field == null)
                return false;

            value = field.GetValue(null);
            return true;
        }

        public static bool IsHdtCoreInitialized(Type coreType)
        {
            if (coreType == null)
                return false;

            if (TryGetStaticFieldValue(coreType, "<Initialized>k__BackingField", out var backingVal))
                return backingVal is bool b && b;

            if (HasMember(coreType, "Initialized", isStatic: true))
                return Convert.ToBoolean(GetStaticMemberValue(coreType, "Initialized") ?? false);

            return true;
        }

        public static bool TryGetHdtCoreObjects(out object game, out object overlay, out bool initializing)
        {
            return TryGetHdtCoreObjectsFromType(FindType("Hearthstone_Deck_Tracker.Core"), out game, out overlay, out initializing);
        }

        internal static bool TryGetHdtCoreObjectsFromType(Type coreType, out object game, out object overlay, out bool initializing)
        {
            game = null;
            overlay = null;
            initializing = false;

            if (coreType == null)
                return false;

            if (!IsHdtCoreInitialized(coreType))
            {
                initializing = true;
                return false;
            }

            // Read backing fields directly so background threads never trigger HDT's lazy
            // Core.Game / Core.Overlay property getters (which construct OverlayWindow and
            // its ViewModels / unfrozen WPF BitmapImage placeholders off the UI thread).
            if (TryGetStaticFieldValue(coreType, "_game", out var gameFieldVal))
                game = gameFieldVal;
            else
                game = GetStaticMemberValue(coreType, "Game");

            if (TryGetStaticFieldValue(coreType, "_overlay", out var overlayFieldVal))
                overlay = overlayFieldVal;
            else
                overlay = GetStaticMemberValue(coreType, "Overlay");

            if (game == null || overlay == null)
            {
                game = null;
                overlay = null;
                initializing = true;
                return false;
            }

            return true;
        }

        public static bool SetStaticMemberValue(Type type, string name, object value)
        {
            if (type == null || string.IsNullOrEmpty(name))
                return false;

            var accessor = GetMemberAccessor(type, name, isStatic: true);
            if (accessor.Property != null && accessor.Property.CanWrite)
            {
                accessor.Property.SetValue(null, value, null);
                return true;
            }

            if (accessor.Field != null)
            {
                accessor.Field.SetValue(null, value);
                return true;
            }

            return false;
        }

        public static MethodInfo FindMethod(Type type, string name, bool isStatic, params Type[] parameterTypes)
        {
            if (type == null || string.IsNullOrEmpty(name))
                return null;

            var flags = isStatic ? AnyStatic : AnyInstance;
            if (parameterTypes != null)
                return type.GetMethod(name, flags, null, parameterTypes, null);

            return type.GetMethod(name, flags);
        }

        public static object InvokeMethod(object instance, string name, params object[] args)
        {
            if (instance == null || string.IsNullOrEmpty(name))
                return null;

            var type = instance.GetType();
            var argCount = args == null ? 0 : args.Length;
            var argTypes = argCount == 0 ? Type.EmptyTypes : new Type[argCount];
            for (var i = 0; i < argCount; i++)
                argTypes[i] = args[i]?.GetType();

            MethodInfo method;
            if (argCount <= 3)
            {
                var key = new MethodKey(type, name, false, argTypes);
                method = MethodCache.GetOrAdd(key, _ => ResolveCompatibleMethod(type, name, false, argTypes));
            }
            else
            {
                method = ResolveCompatibleMethod(type, name, false, argTypes);
            }

            return method?.Invoke(instance, args);
        }

        public static object ResolveGameTag(string tagName)
        {
            if (string.IsNullOrEmpty(tagName))
                return null;

            lock (CacheLock)
            {
                if (GameTagCache.TryGetValue(tagName, out var cached))
                    return cached;
            }

            var gameTagType = FindType("HearthDb.Enums.GameTag");
            if (gameTagType == null || !gameTagType.IsEnum)
                return null;

            try
            {
                var parsed = Enum.Parse(gameTagType, tagName, false);
                lock (CacheLock)
                    GameTagCache[tagName] = parsed;
                return parsed;
            }
            catch
            {
                return null;
            }
        }

        public static bool InvokeOnDispatcher(object target, Action action, int timeoutMs = 1500)
        {
            if (action == null)
                return false;

            void SafeAction()
            {
                try
                {
                    action();
                }
                catch (Exception ex)
                {
                    BridgeLogger.ErrorThrottled("dispatcher_action", "InvokeOnDispatcher", ex, 10);
                }
            }

            var dispatcherObj = target as DispatcherObject;
            var dispatcher = dispatcherObj?.Dispatcher;
            if (dispatcher == null && target == null)
                dispatcher = System.Windows.Application.Current?.Dispatcher;
            if (dispatcher == null || dispatcher.CheckAccess())
            {
                SafeAction();
                return true;
            }

            if (dispatcher.HasShutdownStarted || dispatcher.HasShutdownFinished)
                return false;

            var op = dispatcher.BeginInvoke((Action)SafeAction);
            var status = op.Wait(TimeSpan.FromMilliseconds(timeoutMs));
            if (status != DispatcherOperationStatus.Completed)
            {
                try
                {
                    op.Abort();
                }
                catch
                {
                    // Operation may have already begun executing.
                }
                return false;
            }
            return true;
        }

        private static MemberAccessor GetMemberAccessor(Type ownerType, string name, bool isStatic)
        {
            var key = new MemberKey(ownerType, name, isStatic);
            return MemberCache.GetOrAdd(key, k =>
            {
                var flags = k.IsStatic ? AnyStatic : AnyInstance;
                var prop = GetPropertyHierarchy(k.Owner, k.Name, flags);
                if (prop != null && prop.GetIndexParameters().Length == 0)
                    return new MemberAccessor { Property = prop };

                var field = GetFieldHierarchy(k.Owner, k.Name, flags);
                return new MemberAccessor { Field = field };
            });
        }

        private static MethodInfo ResolveCompatibleMethod(Type type, string name, bool isStatic, Type[] argTypes)
        {
            var flags = isStatic ? AnyStatic : AnyInstance;
            MethodInfo exactMatch = null;
            MethodInfo assignableMatch = null;

            foreach (var candidate in type.GetMethods(flags))
            {
                if (!string.Equals(candidate.Name, name, StringComparison.Ordinal))
                    continue;
                if (candidate.IsGenericMethodDefinition)
                    continue;

                var parameters = candidate.GetParameters();
                if (parameters.Length != argTypes.Length)
                    continue;

                var allExact = true;
                var allCompatible = true;
                for (var i = 0; i < parameters.Length; i++)
                {
                    var paramType = parameters[i].ParameterType;
                    var argType = argTypes[i];
                    if (argType == null)
                    {
                        if (paramType.IsValueType && Nullable.GetUnderlyingType(paramType) == null)
                        {
                            allCompatible = false;
                            break;
                        }
                        allExact = false;
                        continue;
                    }

                    if (paramType == argType)
                        continue;

                    allExact = false;
                    if (!paramType.IsAssignableFrom(argType))
                    {
                        allCompatible = false;
                        break;
                    }
                }

                if (!allCompatible)
                    continue;

                if (allExact)
                {
                    exactMatch = candidate;
                    break;
                }

                if (assignableMatch == null)
                    assignableMatch = candidate;
            }

            return exactMatch ?? assignableMatch;
        }

        private static PropertyInfo GetPropertyHierarchy(Type type, string name, BindingFlags flags)
        {
            for (var current = type; current != null; current = current.BaseType)
            {
                var prop = current.GetProperty(name, flags | BindingFlags.DeclaredOnly);
                if (prop != null && prop.GetIndexParameters().Length == 0)
                    return prop;
            }

            return type.GetProperty(name, flags);
        }

        private static FieldInfo GetFieldHierarchy(Type type, string name, BindingFlags flags)
        {
            for (var current = type; current != null; current = current.BaseType)
            {
                var field = current.GetField(name, flags | BindingFlags.DeclaredOnly);
                if (field != null)
                    return field;
            }

            return type.GetField(name, flags);
        }
    }
}
