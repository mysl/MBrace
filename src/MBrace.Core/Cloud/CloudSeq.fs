﻿namespace Nessos.MBrace
    
    open System
    
    open Nessos.MBrace.Core

    type CloudSeq =
        static member New<'T>(container : string, values : seq<'T>) : ICloud<ICloudSeq<'T>> =
            CloudExpr.wrap <| NewCloudSeqByNameExpr (container, values :> System.Collections.IEnumerable, typeof<'T>)

        static member New<'T>(values : seq<'T>) : ICloud<ICloudSeq<'T>> = 
            cloud {
                let! pid = Cloud.GetProcessId()
                return! CloudSeq.New<'T>(sprintf "process%d" pid, values)
            }

        static member Read<'T>(sequence : ICloudSeq<'T>) : ICloud<seq<'T>> =
            cloud { return sequence :> _ }

        static member Get(container : string) : ICloud<ICloudSeq []> =
            CloudExpr.wrap <| GetCloudSeqsByNameExpr (container)

        static member Get<'T>(container : string, id : string) : ICloud<ICloudSeq<'T>> =
            CloudExpr.wrap <| GetCloudSeqByNameExpr (container, id, typeof<'T>)

        static member TryNew<'T>(container : string, values : seq<'T>) : ICloud<ICloudSeq<'T> option> =
            mkTry<StoreException, _> <| CloudSeq.New(container, values)

        static member TryGet(container : string) : ICloud<ICloudSeq [] option> =
            mkTry<StoreException, _> <| CloudSeq.Get(container)

        static member TryGet<'T>(container : string, id : string) : ICloud<ICloudSeq<'T> option> =
            mkTry<StoreException, _> <| CloudSeq.Get<'T>(container, id)

        static member TryRead<'T>(sequence : ICloudSeq<'T>) : ICloud<seq<'T> option> =
            mkTry<StoreException, _> <| CloudSeq.Read(sequence)

