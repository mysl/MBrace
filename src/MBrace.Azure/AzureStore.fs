﻿namespace Nessos.MBrace.Azure

    open System
    open System.IO
    open System.Runtime.Serialization

    open Microsoft.WindowsAzure.Storage

    open Nessos.MBrace.Store
    open Nessos.MBrace.Azure.Common

    /// <summary>
    ///     Azure Storage provider for MBrace.
    /// </summary>
    type AzureStore private (connectionString : string) =

        let account = CloudStorageAccount.Parse(connectionString) 
        let immutableStore = ImmutableStore(account)
        let mutableStore = MutableStore(account)
        let generalStore = GeneralPurpose(account)

        /// <summary>
        ///     Initialize a new AzureStore instance with given connection string
        /// </summary>
        /// <param name="connectionString">Azure store connection string.</param>
        static member Create(connectionString : string) =
            // Check connection string and connectivity
            try
                let acc = CloudStorageAccount.Parse connectionString 
                (Clients.getBlobClient  acc).GetContainerReference("azurestorecheck").Exists() |> ignore
                (Clients.getTableClient acc).GetTableReference("azurestorecheck").Exists() |> ignore
            with ex -> raise <| new Exception("Failed to create AzureStore", ex)
            
            new AzureStore(connectionString)
        
        /// <summary>
        ///     Initialize an Azure Store instance with given credentials
        /// </summary>
        /// <param name="accountName">Azure account name.</param>
        /// <param name="accountKey">Azure account key base64 encoding.</param>
        /// <param name="endpointsProtocol">Endpoints protocol. Defaults to https.</param>
        static member Create(accountName : string, accountKey : string, ?endpointsProtocol) =
            let endpointsProtocol = defaultArg endpointsProtocol "https"
            let connectionString = 
                sprintf "DefaultEndpointsProtocol=%s;AccountName=%s;AccountKey=%s" 
                    endpointsProtocol accountName accountKey

            AzureStore.Create(connectionString)

        interface ICloudStore with
            member this.Name = "AzureStore"
            member this.Id = sprintf "BlobEndpoint : %A; TableEndpoint : %A" account.BlobEndpoint account.TableEndpoint 

            member this.GetStoreConfiguration () = new AzureStoreConfiguration(connectionString) :> ICloudStoreConfiguration

            // Immutable
            member this.CreateImmutable(folder, file, serialize, asFile) =
                Validation.checkFolder folder
                Validation.checkFile file
                immutableStore.Create(folder, file, serialize, asFile)

            member this.ReadImmutable(folder, file) : Async<Stream> =
                immutableStore.Read(folder, file)

            member this.CopyFrom(folder, file, source, asFile) =
                Validation.checkFolder folder
                Validation.checkFile file
                immutableStore.CopyFrom(folder, file, source, asFile)

            member this.CopyTo(folder, file, target) =
                immutableStore.CopyTo(folder, file, target)

            // Mutable
            member this.CreateMutable(folder, file, serialize) =
                Validation.checkFolder folder
                Validation.checkFile file
                mutableStore.Create(folder, file, serialize)

            member this.ReadMutable(folder, file) =
                mutableStore.Read(folder, file)

            member this.TryUpdateMutable(folder, file, serialize, etag) : Async<bool * Tag> =
                mutableStore.Update(folder, file, serialize, etag)

            member this.ForceUpdateMutable(folder, file, serialize) : Async<Tag> =
                async {
                    let! success, newTag = mutableStore.Update(folder, file, serialize)
                    if success then return newTag
                    else return failwithf "Cannot force update %s - %s" folder file
                }

            // General purpose
            member this.Exists(folder, file) =
                generalStore.Exists(folder, file)

            member this.ContainerExists(folder) =
                generalStore.Exists(folder)

            member this.EnumerateFiles(folder) =
                generalStore.GetFiles(folder)

            member this.EnumerateContainers () =
                generalStore.GetFolders ()

            member this.DeleteContainer(folder) =
                async {
                    try
                        do! generalStore.Delete(folder)
                    with ex ->
                        raise <| Exception(sprintf "Cannot delete container %s" folder, ex)
                }

            member this.Delete(folder, file) =
                async {
                    try
                        do! generalStore.Delete(folder, file)
                    with ex ->
                        raise <| Exception(sprintf "Cannot delete %s - %s" folder file, ex)
                }


    and internal AzureStoreConfiguration (connectionString : string) =

        private new (si : SerializationInfo, _ : StreamingContext) =
            let connectionString = si.GetValue("connectionString", typeof<string>) :?> string
            new AzureStoreConfiguration(connectionString)

        interface ISerializable with
            member __.GetObjectData(si : SerializationInfo, _ : StreamingContext) =
                si.AddValue("connectionString", connectionString)

        interface ICloudStoreConfiguration with
            member this.Name = "AzureStore"
            member this.Id = connectionString
            member this.Init () = AzureStore.Create connectionString :> ICloudStore