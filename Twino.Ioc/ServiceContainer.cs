﻿using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Threading.Tasks;
using Twino.Ioc.Pool;

namespace Twino.Ioc
{
    /// <summary>
    /// Default service container of Twino MVC for dependency inversion
    /// </summary>
    public class ServiceContainer : IServiceContainer
    {
        /// <summary>
        /// Service descriptor items
        /// </summary>
        private Dictionary<Type, ServiceDescriptor> Items { get; set; }

        public ServiceContainer()
        {
            Items = new Dictionary<Type, ServiceDescriptor>();
        }

        #region Add

        /// <summary>
        /// Adds a service to the container
        /// </summary>
        public void AddTransient<TService, TImplementation>()
            where TService : class
            where TImplementation : class, TService
        {
            AddTransient(typeof(TService), typeof(TImplementation));
        }

        /// <summary>
        /// Adds a service to the container
        /// </summary>
        public void AddTransient(Type serviceType, Type implementationType)
        {
            if (Items.ContainsKey(serviceType))
                throw new InvalidOperationException("Specified service type is already added into service container");

            ServiceDescriptor descriptor = new ServiceDescriptor
                                           {
                                               ServiceType = serviceType,
                                               ImplementationType = implementationType,
                                               Instance = null,
                                               Implementation = ImplementationType.Transient
                                           };

            Items.Add(serviceType, descriptor);
        }

        /// <summary>
        /// Adds a service to the container
        /// </summary>
        public void AddScoped<TService, TImplementation>()
            where TService : class
            where TImplementation : class, TService
        {
            AddScoped(typeof(TService), typeof(TImplementation));
        }

        /// <summary>
        /// Adds a service to the container
        /// </summary>
        public void AddScoped(Type serviceType, Type implementationType)
        {
            if (Items.ContainsKey(serviceType))
                throw new InvalidOperationException("Specified service type is already added into service container");

            ServiceDescriptor descriptor = new ServiceDescriptor
                                           {
                                               ServiceType = serviceType,
                                               ImplementationType = implementationType,
                                               Instance = null,
                                               Implementation = ImplementationType.Scoped
                                           };

            Items.Add(serviceType, descriptor);
        }

        /// <summary>
        /// Adds a singleton service to the container.
        /// Service will be created with first call.
        /// </summary>
        public void AddSingleton<TService, TImplementation>()
            where TService : class
            where TImplementation : class, TService
        {
            AddSingleton(typeof(TService), typeof(TImplementation));
        }

        /// <summary>
        /// Adds a singleton service with instance to the container.
        /// </summary>
        public void AddSingleton<TService, TImplementation>(TImplementation instance)
            where TService : class
            where TImplementation : class, TService
        {
            AddSingleton(typeof(TService), instance);
        }

        /// <summary>
        /// Adds a singleton service to the container.
        /// Service will be created with first call.
        /// </summary>
        public void AddSingleton(Type serviceType, Type implementationType)
        {
            ServiceDescriptor descriptor = new ServiceDescriptor
                                           {
                                               ServiceType = serviceType,
                                               ImplementationType = implementationType,
                                               Instance = null,
                                               Implementation = ImplementationType.Singleton
                                           };

            Items.Add(serviceType, descriptor);
        }

        /// <summary>
        /// Adds a singleton service with instance to the container.
        /// </summary>
        public void AddSingleton(Type serviceType, object instance)
        {
            Type implementationType = instance.GetType();

            ServiceDescriptor descriptor = new ServiceDescriptor
                                           {
                                               ServiceType = serviceType,
                                               ImplementationType = implementationType,
                                               Instance = instance,
                                               Implementation = ImplementationType.Singleton
                                           };

            Items.Add(serviceType, descriptor);
        }

        /// <summary>
        /// Adds a service pool to the container
        /// </summary>
        /// <param name="options">Options function</param>
        public void AddPool<TService>(Action<ServicePoolOptions> options) where TService : class
        {
            AddPool<TService>(options, null);
        }

        /// <summary>
        /// Adds a service pool to the container
        /// </summary>
        /// <param name="options">Options function</param>
        /// <param name="instance">After each instance is created, to do custom initialization, this method will be called.</param>
        public void AddPool<TService>(Action<ServicePoolOptions> options, Action<TService> instance) where TService : class
        {
            ServicePool<TService> pool = new ServicePool<TService>(this, options, instance);

            ServiceDescriptor descriptor = new ServiceDescriptor
                                           {
                                               ServiceType = typeof(TService),
                                               ImplementationType = typeof(ServicePool<TService>),
                                               Instance = pool,
                                               Implementation = ImplementationType.Pool
                                           };

            Items.Add(typeof(TService), descriptor);
        }

        #endregion

        #region Get

        /// <summary>
        /// Gets the service from the container.
        /// </summary>
        public async Task<TService> Get<TService>(IContainerScope scope = null)
            where TService : class
        {
            object o = await Get(typeof(TService), scope);
            return (TService) o;
        }

        /// <summary>
        /// Gets the service from the container.
        /// </summary>
        public async Task<object> Get(Type serviceType, IContainerScope scope = null)
        {
            ServiceDescriptor descriptor = GetDescriptor(serviceType);

            switch (descriptor.Implementation)
            {
                //create new instance
                case ImplementationType.Transient:
                    return await CreateInstance(descriptor.ImplementationType, scope);

                case ImplementationType.Scoped:
                    if (scope == null)
                        throw new InvalidOperationException("Type is registered as Scoped but scope parameter is null for IServiceContainer.Get method");

                    return await scope.Get(descriptor, this);

                case ImplementationType.Singleton:
                    //if instance already created return
                    if (descriptor.Instance != null)
                        return descriptor.Instance;

                    //create instance for first time and set Instance property of descriptor to prevent re-create for next times
                    object instance = await CreateInstance(descriptor.ImplementationType, scope);
                    descriptor.Instance = instance;
                    return instance;

                case ImplementationType.Pool:
                    IServicePool pool = (IServicePool) descriptor.Instance;
                    PoolServiceDescriptor pdesc = await pool.GetAndLock();
                    return pdesc.GetInstance();

                default:
                    return null;
            }
        }

        /// <summary>
        /// Gets descriptor of type
        /// </summary>
        public ServiceDescriptor GetDescriptor<TService>()
        {
            return GetDescriptor(typeof(TService));
        }

        /// <summary>
        /// Gets descriptor of type
        /// </summary>
        public ServiceDescriptor GetDescriptor(Type serviceType)
        {
            ServiceDescriptor descriptor;

            //finds by service type
            if (Items.ContainsKey(serviceType))
                descriptor = Items[serviceType];

            //if could not find by service type, tries to find by implementation type
            else
                descriptor = Items.Values.FirstOrDefault(x => x.ImplementationType == serviceType);

            if (descriptor == null)
                throw new KeyNotFoundException("Service type is not found");

            return descriptor;
        }

        /// <summary>
        /// Creates instance of type.
        /// If it has constructor parameters, finds these parameters from the container
        /// </summary>
        public async Task<object> CreateInstance(Type type, IContainerScope scope = null)
        {
            ConstructorInfo constructor = type.GetConstructors()[0];
            ParameterInfo[] parameters = constructor.GetParameters();

            //if parameterless create directly and return
            if (parameters.Length == 0)
                return Activator.CreateInstance(type);

            object[] values = new object[parameters.Length];

            //find all parameters from the container
            for (int i = 0; i < parameters.Length; i++)
            {
                ParameterInfo parameter = parameters[i];
                object value = await Get(parameter.ParameterType, scope);
                values[i] = value;
            }

            //create with parameters found from the container
            return Activator.CreateInstance(type, values);
        }

        /// <summary>
        /// Creates new scope belong this container.
        /// </summary>
        public IContainerScope CreateScope()
        {
            return new DefaultContainerScope();
        }

        #endregion

        #region Remove

        /// <summary>
        /// Removes the service from the container
        /// </summary>
        public void Remove<TService>()
            where TService : class
        {
            Remove(typeof(TService));
        }

        /// <summary>
        /// Removes the service from the container
        /// </summary>
        public void Remove(Type type)
        {
            if (Items.ContainsKey(type))
                Items.Remove(type);
        }

        #endregion
    }
}