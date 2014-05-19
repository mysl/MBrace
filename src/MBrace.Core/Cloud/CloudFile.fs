﻿namespace Nessos.MBrace
    
    open System
    open System.IO
    open System.Collections
    open System.Collections.Generic
    open System.Runtime.Serialization

    open Nessos.MBrace.Core

    type CloudFile = 

        static member Create(container : string, name : string, serialize : (Stream -> Async<unit>)) : ICloud<ICloudFile> =
            CloudExpr.wrap <| NewCloudFile(container, name, serialize)

        static member Create(container : string, serialize : (Stream -> Async<unit>)) : ICloud<ICloudFile> =
            cloud {
                return! CloudFile.Create(container, Guid.NewGuid().ToString(), serialize)
            }

        static member Create(serialize : (Stream -> Async<unit>)) : ICloud<ICloudFile> =
            cloud {
                let! pid = Cloud.GetProcessId()
                return! CloudFile.Create(sprintf "process%d" pid, serialize)
            }

        static member Read(cloudFile : ICloudFile, deserialize : (Stream -> Async<'Result>)) : ICloud<'Result> =
            let deserialize stream = async { let! o = deserialize stream in return o :> obj }
            CloudExpr.wrap <| ReadCloudFile(cloudFile, deserialize, typeof<'Result>)

        // this should probably move to MBrace.Lib

        static member ReadAsSeq(cloudFile:ICloudFile, deserializer :(Stream -> Async<seq<'T>>)) : ICloud<ICloudSeq<'T>> =
            cloud { return new CloudFileSequence<'T>(cloudFile, deserializer) :> ICloudSeq<'T> }

        static member Get(container : string, name : string) : ICloud<ICloudFile> =
            CloudExpr.wrap <| GetCloudFile(container, name)

        static member Get(container : string) : ICloud<ICloudFile []> =
            CloudExpr.wrap <| GetCloudFiles(container)

        static member TryCreate(container : string, name : string, serialize : (Stream -> Async<unit>)) : ICloud<ICloudFile option> =
            mkTry<StoreException,_> <| CloudFile.Create(container, name, serialize)

        static member TryGet(container : string, name : string) : ICloud<ICloudFile option> =
            mkTry<StoreException,_> <| CloudFile.Get(container,name)

        static member TryGet(container : string) : ICloud<ICloudFile [] option> =
            mkTry<StoreException,_> <| CloudFile.Get(container)

        static member TryRead(cloudFile : ICloudFile, deserialize : (Stream -> Async<'Result>)) : ICloud<'Result option> =
            mkTry<StoreException,_> <| CloudFile.Read(cloudFile, deserialize)

    // TODO: this is non-essential; could be moved to MBrace.Lib

    and CloudFileSequence<'T>(file : ICloudFile, reader : Stream -> Async<seq<'T>>) =

        let enumerate () = 
            async {
                let! stream = file.Read()
                let! seq = reader stream
                return seq.GetEnumerator()
            } |> Async.RunSynchronously
        
        interface ICloudSeq<'T> with
            member __.Name = file.Name
            member __.Container = file.Container
            member __.Type = typeof<'T>
            member __.Count = raise <| new NotSupportedException()
            member __.Size = file.Size
            member __.Dispose() = async.Zero()

        interface IEnumerable<'T> with
            member __.GetEnumerator() : IEnumerator = enumerate() :> _
            member __.GetEnumerator() = enumerate()

        interface ISerializable with
            member __.GetObjectData(sI : SerializationInfo, _ : StreamingContext) =
                sI.AddValue("file", file)
                sI.AddValue("reader", reader)

        new (sI : SerializationInfo, _ : StreamingContext) =
            let file = sI.GetValue("file", typeof<ICloudFile>) :?> ICloudFile
            let deserializer = sI.GetValue("reader", typeof<Stream -> Async<seq<'T>>>) :?> Stream -> Async<seq<'T>>
            new CloudFileSequence<'T>(file, deserializer)