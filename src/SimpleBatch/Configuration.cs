﻿using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Threading;
using SimpleBatch;

namespace SimpleBatch
{
    // Argument can implement this to be self describing. 
    // This can be queried on another thread. 
    public interface ISelfWatch
    {
        // $$$ Expected that string is a single line. 
        // Use "; " to denote multiple lines. 
        string GetStatus();
    }

    public class BindResult
    {
        // The actual object passed into the user's method.
        // This can be updated on function return if the parameter is an out-parameter.
        public object Result { get; set; }

        // A self-watch on the object for providing real-time status information
        // about how that object is being used.
        public virtual ISelfWatch Watcher
        {
            get
            {
                return this.Result as ISelfWatch;
            }
        }

        // A cleanup action called for this parameter after the function returns.
        // Called in both regular and exceptional cases. 
        // This can do things like close a stream, or queue a message.
        public virtual void OnPostAction()
        {
            // default is nop
        }
    }     
   
    // Get the function instance guid for the currently executing function 
    public interface IContext
    {
        Guid FunctionInstanceGuid { get; }
    }

    // Public one that we bind to. Simpler, doesn't expose a BindResult. 
    public interface IBinder
    {
        T Bind<T>(Attribute a);
        string AccountConnectionString { get; }
    }

    // $$$ Remove this one and merge with IBinder. 
    // Internal one, exposes the BindResult.
    public interface IBinderEx
    {
        BindResult<T> Bind<T>(Attribute a);
        string AccountConnectionString { get; }
        Guid FunctionInstanceGuid { get; }
    }

    public static class IBinderExtensions
    {
        // Get a stream for the given blob. The storage account is relative to binder.AccountConnetionString,
        // and the container and blob name are specified.
        public static BindResult<T> BindReadStream<T>(this IBinderEx binder, string containerName, string blobName)
        {
            return binder.Bind<T>(new BlobInputAttribute(Combine(containerName, blobName)));
        }

        public static BindResult<T> BindWriteStream<T>(this IBinderEx binder, string containerName, string blobName)
        {
            return binder.Bind<T>(new BlobOutputAttribute(Combine(containerName, blobName)));
        }

        public static T BindReadStream<T>(this IBinder binder, string containerName, string blobName)
        {
            return binder.Bind<T>(new BlobInputAttribute(Combine(containerName, blobName)));
        }

        public static T BindWriteStream<T>(this IBinder binder, string containerName, string blobName)
        {
            return binder.Bind<T>(new BlobOutputAttribute(Combine(containerName, blobName)));
        }

        // Combine container and Blob name into a single path. 
        // This is the inverse of CloudBlobPath
        private static string Combine(string containerName, string blobName)
        {
            // $$$ Validate the names upfront where it's easy to diagnose. This can avoid cryptor 400 errors from Azure later. 
            // Rules are here: http://msdn.microsoft.com/en-us/library/windowsazure/dd135715.aspx
            return containerName + @"\" + blobName;
        }
    
    }

    // Strongly typed wrapper around a result.
    // This is useful in model binders.
    // ### Call cleanup function? And how does that interfere with out parameter?
    public class BindResult<T> : BindResult
    {
        private readonly BindResult[] _inners;
        public Action<T> Cleanup;
        public ISelfWatch _watcher;

        // this OnPostAction() will chain to inners
        public BindResult(T result, params BindResult[] inners)
        {
            _inners = inners;
            this.Result = result;
        }

        public BindResult(T result)
        {
            this.Result = result;
        }

        public override ISelfWatch Watcher
        {
            get
            {
                return base.Watcher ?? _inners[0].Watcher;
            }
        }

        public new T Result
        {
            get
            {
                BindResult x = this;
                return (T)x.Result;
            }
            set
            {
                BindResult x = this;
                x.Result = value;
            }
        }

        public override void OnPostAction()
        {
            if (Cleanup != null)
            {
                Cleanup(this.Result);
            }

            if (_inners != null)
            {
                foreach (var inner in _inners)
                {
                    inner.OnPostAction();
                }
            }
        }
    }

   

    // Note the binders here don't depend on a specific azure client library
    // So use generic things like connection strings instead of CloudStorageAccount classes.
    // This is because this assembly is included by user code, and the user may pick any azure client library.

    // Bind a CloudBlob to something more structured.
    // Use CloudBlob instead of Stream so that we have metadata, filename.
    public interface ICloudBlobBinder
    {
        // Returned object should be assignable to target type.
        BindResult Bind(IBinderEx binder, string containerName, string blobName, Type targetType);
    }

    public interface ICloudBlobBinderProvider
    {
        // Can this binder read/write the given type?
        // This could be a straight type match, or a generic type.
        ICloudBlobBinder TryGetBinder(Type targetType, bool isInput);
    }

    public interface ICloudTableBinder
    {
        BindResult Bind(IBinderEx bindingContext, Type targetType, string tableName);
    }
    public interface ICloudTableBinderProvider
    {
        // isReadOnly - True if we know we want it read only (must have been specified in the attribute).
        ICloudTableBinder TryGetBinder(Type targetType, bool isReadOnly);
    }

    // Binds to arbitrary entities in the cloud
    public interface ICloudBinder
    {
        BindResult Bind(IBinderEx bindingContext, ParameterInfo parameter);
    }

    // Binder for any arbitrary azure things. Could even bind to multiple things. 
    // No meta infromation here, so we can't reason anything about it (Reader, writer, etc)
    public interface ICloudBinderProvider
    {
        ICloudBinder TryGetBinder(Type targetType);
    }

    public interface IConfiguration
    {
        // Could cache a wrapper directly binding against IClourBlobBinder.
        IList<ICloudBlobBinderProvider> BlobBinders { get; }

        IList<ICloudTableBinderProvider> TableBinders { get; }

        // General type binding. 
        IList<ICloudBinderProvider> Binders { get; }
    }

    public interface IFluentConfig
    {
        IFluentConfig Bind(string parameterName, Attribute binderAttribute);
    }
    
    // Extension methods to provide a convenience operation over binding
    public static class IFluentConfigExtensions
    {
        public static IFluentConfig Description(this IFluentConfig config, string description)
        {
            return config.Bind(null, new DescriptionAttribute(description));
        }

        public static IFluentConfig TriggerNoAutomatic(this IFluentConfig config)
        {
            return config.Bind(null, new NoAutomaticTriggerAttribute());
        }

        public static IFluentConfig TriggerTimer(this IFluentConfig config, TimeSpan interval)
        {
            return config.Bind(null, new TimerAttribute(interval.ToString()));
        }

        public static IFluentConfig BindConfig(this IFluentConfig config, string parameterName, string filename)
        {
            return config.Bind(parameterName, new ConfigAttribute(filename));
        }

        public static IFluentConfig BindBlobInput(this IFluentConfig config, string parameterName, string path)
        {
            return config.Bind(parameterName, new BlobInputAttribute(path));
        }

        public static IFluentConfig BindBlobOutput(this IFluentConfig config, string parameterName, string path)
        {
            return config.Bind(parameterName, new BlobOutputAttribute(path));
        }

        public static IFluentConfig BindTable(this IFluentConfig config, string parameterName, string tableName)
        {
            return config.Bind(parameterName, new TableAttribute(tableName));
        }
    }
}