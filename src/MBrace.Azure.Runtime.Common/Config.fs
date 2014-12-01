﻿namespace Nessos.MBrace.Azure.Runtime

open System
open System.Reflection
open System.Threading
open System.Security.Cryptography
open System.Text

open Nessos.Vagrant
open Nessos.MBrace.Runtime
open Nessos.FsPickler
open Microsoft.WindowsAzure.Storage
open Microsoft.ServiceBus
open Microsoft.ServiceBus.Messaging
open System.Collections.Concurrent


/// Configuration identifier.
type ConfigurationId = 
    private
    | ConfigurationId of hashcode : byte []

    static member internal ofText (txt : string) = 
        let hashAlgorithm = SHA256Managed.Create()
        ConfigurationId(hashAlgorithm.ComputeHash(Encoding.UTF8.GetBytes txt))

/// Azure specific Runtime Configuration.
type Configuration = 
    { /// Azure storage connection string.
      StorageConnectionString : string
      /// Service Bus connection string.
      ServiceBusConnectionString : string
      /// Default Blob/Table storage container and table name
      DefaultTableOrContainer : string
      /// Default Service Bus queue name.
      DefaultQueue : string
      /// Default Table name for logging.
      DefaultLogTable : string
      /// Default Topic name.
      DefaultTopic : string }

    /// Returns an Azure Configuration with the default table, queue, container values and
    /// sample connection strings.
    static member Default = 
        { StorageConnectionString = 
            "DefaultEndpointsProtocol=[https];AccountName=[myAccount];AccountKey=[myKey];"
          ServiceBusConnectionString = 
              "Endpoint=sb://[your namespace].servicebus.windows.net;SharedAccessKeyName=RootManageSharedAccessKey;SharedAccessKey=[your secret]"
          DefaultTableOrContainer = "mbraceruntime"
          DefaultQueue = "mbraceruntimetaskqueue"
          DefaultTopic = "mbraceruntimetasktopic"
          DefaultLogTable = "mbraceruntimelogs" }

    /// Configuration identifier hash.
    member this.ConfigurationId : ConfigurationId = 
        this.GetType()
        |> Microsoft.FSharp.Reflection.FSharpType.GetRecordFields
        |> Seq.map (fun pi -> pi.GetValue(this) :?> string)
        |> String.concat String.Empty
        |> ConfigurationId.ofText 

type internal ClientProvider (config : Configuration) =
    let acc = CloudStorageAccount.Parse(config.StorageConnectionString)
    member __.TableClient = acc.CreateCloudTableClient()
    member __.BlobClient = acc.CreateCloudBlobClient()
    member __.NamespaceClient = NamespaceManager.CreateFromConnectionString(config.ServiceBusConnectionString)
    member __.QueueClient(queue : string) = QueueClient.CreateFromConnectionString(config.ServiceBusConnectionString, queue)
    member __.SubscriptionClient(topic, name) = SubscriptionClient.CreateFromConnectionString(config.ServiceBusConnectionString, topic, name)
    member __.TopicClient(topic) = TopicClient.CreateFromConnectionString(config.ServiceBusConnectionString, topic)

    member __.ClearAll() =
        let _ = __.TableClient.GetTableReference(config.DefaultTableOrContainer).DeleteIfExists()
        let _ = __.TableClient.GetTableReference(config.DefaultLogTable).DeleteIfExists()
        let _ = __.BlobClient.GetContainerReference(config.DefaultTableOrContainer).DeleteIfExists()
        let _ = __.NamespaceClient.DeleteQueue(config.DefaultQueue)
        let _ = __.NamespaceClient.DeleteTopic(config.DefaultTopic)
        ()
    member __.InitAll() =
        let _ = __.TableClient.GetTableReference(config.DefaultTableOrContainer).CreateIfNotExists()
        let _ = __.TableClient.GetTableReference(config.DefaultLogTable).CreateIfNotExists()
        let _ = __.BlobClient.GetContainerReference(config.DefaultTableOrContainer).CreateIfNotExists()
        ()

[<Sealed;AbstractClass>]
/// Holds configuration specific resources.
type ConfigurationRegistry private () =
    static let registry = ConcurrentDictionary<ConfigurationId * Type, obj>()

    static member Register<'T>(config : ConfigurationId, item : 'T) : unit =
        registry.TryAdd((config, typeof<'T>), item :> obj)
        |> ignore

    static member Resolve<'T>(config : ConfigurationId) : 'T =
        match registry.TryGetValue((config, typeof<'T>)) with
        | true, v  -> v :?> 'T
        | false, _ -> failwith "No active configuration found or registered resource"

[<CompilationRepresentation(CompilationRepresentationFlags.ModuleSuffix)>]
module Configuration =
    let private runOnce (f : unit -> 'T) = let v = lazy(f ()) in fun () -> v.Value

    let private init =
        runOnce(fun () ->
            let _ = System.Threading.ThreadPool.SetMinThreads(100, 100)

            // vagrant initialization
            let ignoredAssemblies =
                let this = Assembly.GetExecutingAssembly()
                let dependencies = Utilities.ComputeAssemblyDependencies(this, requireLoadedInAppDomain = false)
                new System.Collections.Generic.HashSet<_>(dependencies)
            let ignoredNames =
                set [ "MBrace.Azure.Runtime"
                      "MBrace.Azure.Runtime.Common"
                      "MBrace.Azure.Client"
                      "MBrace.Azure.Runtime.Standalone"  ]
            let ignore assembly =
                // TODO : change
                ignoredAssemblies.Contains(assembly) || ignoredNames.Contains(assembly.GetName().Name)

            VagrantRegistry.Initialize(ignoreAssembly = ignore, loadPolicy = AssemblyLoadPolicy.ResolveAll))

    /// Default serializer.
    let Serializer = init () ; VagrantRegistry.Pickler

    /// Initialize Vagrant.
    let Initialize () = init ()

    /// Activates the given configuration.
    let Activate(config : Configuration) : unit = 
        init ()
        let cp = new ClientProvider(config)
        cp.InitAll()
        ConfigurationRegistry.Register<ClientProvider>(config.ConfigurationId, cp)

    /// Warning : Deletes all queues, tables and containers described in the given configuration.
    /// Does not delete process created resources.
    let DeleteResources (config : Configuration) : unit =
        init ()
        let cp = ConfigurationRegistry.Resolve<ClientProvider>(config.ConfigurationId)
        cp.ClearAll()