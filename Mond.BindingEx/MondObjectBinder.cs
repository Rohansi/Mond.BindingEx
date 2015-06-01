﻿using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;

namespace Mond.BindingEx
{
    public static class MondObjectBinder
    {
        private static readonly Dictionary<Type, MondValue> BindingCache;

        static MondObjectBinder()
        {
            BindingCache = new Dictionary<Type, MondValue>();
        }

        public static MondValue Bind<T>( MondState state = null, MondBindingOptions options = MondBindingOptions.None )
        {
            var dummy = null as MondValue;
            return Bind( typeof( T ), out dummy, state, options );
        }

        public static MondValue Bind<T>( out MondValue prototype, MondState state = null, MondBindingOptions options = MondBindingOptions.None )
        {
            return Bind( typeof( T ), out prototype, state, options );
        }

        public static MondValue Bind<T>( T instance, MondState state = null, MondBindingOptions options = MondBindingOptions.None )
        {
            return Bind( typeof( T ), instance, state, options );
        }

        public static MondValue Bind<T>( T instance, out MondValue prototype, MondState state = null, MondBindingOptions options = MondBindingOptions.None )
        {
            return Bind( typeof( T ), instance, out prototype, state, options );
        }

        public static MondValue Bind( Type type, object instance, MondState state = null, MondBindingOptions options = MondBindingOptions.None )
        {
            var dummy = null as MondValue;
            return Bind( type, instance, out dummy, state, options );
        }

        public static MondValue Bind( Type type, object instance, out MondValue prototype, MondState state = null, MondBindingOptions options = MondBindingOptions.None )
        {
            if( options.HasFlag( MondBindingOptions.AutoInsert ) )
                throw new ArgumentException( "MondBindingOptions.AutoInsert is not valid when binding object instances" );

            var binding = new MondValue( state );
            var tempBinding = Bind( type, out prototype, state, options );

            binding.Prototype = prototype;
            binding.UserData = instance;

            return binding;
        }

        public static MondValue Bind( Type type, MondState state = null, MondBindingOptions options = MondBindingOptions.None )
        {
            var dummy = null as MondValue;
            return Bind( type, out dummy, state, options );
        }

        public static MondValue Bind( MulticastDelegate function, MondState state = null, MondBindingOptions options = MondBindingOptions.None )
        {
            return Bind( function as Delegate, state, options );
        }

        public static MondValue Bind( Delegate function, MondState state = null, MondBindingOptions options = MondBindingOptions.None )
        {
            return Bind( function, function.Method.Name, state, options );
        }

        public static MondValue Bind( MulticastDelegate function, string name, MondState state = null, MondBindingOptions options = MondBindingOptions.None )
        {
            return Bind( function as Delegate, name, state, options );
        }

        public static MondValue Bind( Delegate function, string name, MondState state = null, MondBindingOptions options = MondBindingOptions.None )
        {
            if( options.HasFlag( MondBindingOptions.AutoLock ) )
                throw new ArgumentException( "MondBindingOptions.AutoLock is not valid when binding delegates", "options" );

            var shim = BindingUtils.CreateStaticMethodShim( function );

            if( options.HasFlag( MondBindingOptions.AutoInsert ) )
            {
                if( state == null )
                    throw new ArgumentNullException( "Must specify a valid MondState when specifying MondBindingOptions.AutoInset", "state" );

                if( String.IsNullOrWhiteSpace( name ) )
                    throw new ArgumentException( "Must provide a valid name when specifying MondBindingOptions.AutoInsert", "name" );

                state[name] = shim;
            }

            return shim;
        }

        public static MondValue Bind( Type type, out MondValue prototype, MondState state = null, MondBindingOptions options = MondBindingOptions.None )
        {
            if( options.HasFlag( MondBindingOptions.AutoInsert ) && state == null )
                throw new ArgumentNullException( "A valid MondState must be given when MondBindingOptions.AutoInsert is present", "state" );

            prototype = null as MondValue;

            var binding = null as MondValue;

            if( BindingCache.ContainsKey( type ) )
            {
                binding = BindingCache[type];
                prototype = binding.Prototype;

                return binding;
            }

            if( type.IsEnum )
                binding = BindEnum( type, state );

            if( type.IsClass || type.IsStruct() )
                binding = BindClass( type, state, out prototype );

            if( options.HasFlag( MondBindingOptions.AutoLock ) )
            {
                if( binding != null && binding.Type == MondValueType.Object )
                    binding.Lock();

                if( prototype != null && prototype.Type == MondValueType.Object )
                    prototype.Lock();
            }

            if( options.HasFlag( MondBindingOptions.AutoInsert ) )
                state[type.Name] = binding;

            BindingCache.Add( type, binding );
            return binding;
        }

        private static MondValue BindEnum( Type type, MondState state )
        {
            var binding = new MondValue( state );
            var pairs = Enum.GetNames( type ).Zip(
                            Enum.GetValues( type )
                                .AsEnumerable()
                                .Select( v => Convert.ChangeType( v, typeof( double ) ) )
                                .Cast<double>(),
                            ( a, b ) => new { Name = a, Value = b }
                        );

            foreach( var pair in pairs )
                binding[pair.Name] = pair.Value;

            binding["hasFlag"] = BindingUtils.CreateStaticMethodShim( typeof( MondHelperMethods ), "EnumHasFlag" );

            return binding;
        }

        private static MondValue BindClass( Type type, MondState state, out MondValue prototype )
        {
            if( type.IsAbstract && !type.IsSealed )
                throw new ArgumentException( "Cannot bind abstract classes", "type" );

            prototype = new MondValue( state );
            var binding = new MondValue( state );
            var methodComparer = new MethodNameComparer();
            var propertyComparer = new PropertyNameComparer();
            var isStatic = type.IsSealed && type.IsAbstract;
            var methods = null as IEnumerable<MethodInfo>;
            var properties = null as IEnumerable<PropertyInfo>;

            if( isStatic )
                prototype = null;

            Func<MethodInfo, bool> IsOperator = m => m.GetCustomAttribute<MondOperatorAttribute>() != null;
            Func<MethodInfo, bool> IsProperty = m => m.IsSpecialName && ( m.Name.StartsWith( "get_" ) || m.Name.StartsWith( "set_" ) );
            Func<MemberInfo, bool> ShouldIgnore = m => m.GetCustomAttribute<MondIgnoreAttribute>() != null;

            if( !isStatic )
            {
                // Hook up instance methods
                methods = type.GetMethods( BindingFlags.Public | BindingFlags.Instance )
                              .Reject( IsOperator )
                              .Reject( IsProperty )
                              .Reject( m => ShouldIgnore( m ) )
                              .Distinct( methodComparer );

                foreach( var method in methods )
                {
                    // Ignore the methods inherited from System.Object
                    // ToString will get aliased to the __string metamethod later
                    if( /* method.Name == "ToString" || */ method.Name == "GetHashCode" || method.Name == "Equals" || method.Name == "GetType" )
                        continue;

                    var shim = BindingUtils.CreateInstanceMethodShim( type, method.Name );
                    prototype[method.Name] = shim;
                }

                if( !methods.Any( m => m.Name == "__string" ) )
                {
                    var shim = BindingUtils.CreateInstanceMethodShim( type, "ToString" );
                    prototype["__string"] = shim;
                }
            }

            // Hook up static methods
            methods = type.GetMethods( BindingFlags.Public | BindingFlags.Static )
                          .Reject( IsOperator )
                          .Reject( IsProperty )
                          .Reject( m => ShouldIgnore( m ) )
                          .Distinct( methodComparer );

            foreach( var method in methods )
            {
                var shim = BindingUtils.CreateStaticMethodShim( type, method.Name );
                binding[method.Name] = shim;
            }

            // Hook up user defined operators
            methods = type.GetMethods( BindingFlags.Public | BindingFlags.Static )
                          .Where( IsOperator )
                          .Reject( m => ShouldIgnore( m ) )
                          .Distinct( new OperatorAttributeComparer() );

            if( !methods.Any() && state == null )
                throw new ArgumentException( "Must provide a valid MondState when attempting to bind user defined operators", "state" );

            foreach( var method in methods )
            {
                var attr = method.GetCustomAttribute<MondOperatorAttribute>();
                var shim = BindingUtils.CreateStaticMethodShim( type, method.Name );
                state["__ops"][attr.Operator] = shim;
            }

            if( !isStatic )
            {
                // Hook up instance properties
                properties = type.GetProperties( BindingFlags.Public | BindingFlags.Instance )
                                 .Reject( m => ShouldIgnore( m ) )
                                 .Distinct( propertyComparer );

                foreach( var prop in properties )
                {
                    var method = null as MethodInfo;
                    var shim = null as MondInstanceFunction;
                    var name = null as string;

                    if( ( method = prop.GetGetMethod() ) != null )
                    {
                        shim = BindingUtils.CreateInstanceMethodShim( type, method.Name );
                        name = "get{0}".With( prop.Name );
                        prototype[name] = shim;
                    }

                    if( ( method = prop.GetSetMethod() ) != null )
                    {
                        shim = BindingUtils.CreateInstanceMethodShim( type, method.Name );
                        name = "set{0}".With( prop.Name );
                        prototype[name] = shim;
                    }
                }
            }

            // Hook up static properties
            properties = type.GetProperties( BindingFlags.Public | BindingFlags.Static )
                             .Reject( m => ShouldIgnore( m ) )
                             .Distinct( propertyComparer );

            foreach( var prop in properties )
            {
                var method = null as MethodInfo;
                var shim = null as MondFunction;
                var name = null as string;

                if( ( method = prop.GetGetMethod() ) != null )
                {
                    shim = BindingUtils.CreateStaticMethodShim( type, method.Name );
                    name = "get{0}".With( prop.Name );
                    binding[name] = shim;
                }

                if( ( method = prop.GetSetMethod() ) != null )
                {
                    shim = BindingUtils.CreateStaticMethodShim( type, method.Name );
                    name = "set{0}".With( prop.Name );
                    binding[name] = shim;
                }
            }

            if( !isStatic )
                // Hook up the constructor
                binding["new"] = BindingUtils.CreateConstructorShim( type, prototype );

            return binding;
        }
    }
}
