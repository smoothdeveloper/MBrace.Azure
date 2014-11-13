﻿namespace Nessos.MBrace.Azure.Runtime.Resources

open System
open System.Runtime.Serialization
open Microsoft.WindowsAzure.Storage
open Nessos.MBrace.Azure.Runtime
open Nessos.MBrace.Azure.Runtime.Common
open Nessos.MBrace.Runtime

/// Result value
type Result<'T> =
    | Completed of 'T
    | Exception of ExceptionDispatchInfo
    | Cancelled of ExceptionDispatchInfo<OperationCanceledException>
with
    member inline r.Value =
        match r with
        | Completed t -> t
        | Exception e -> ExceptionDispatchInfo.raise true e
        | Cancelled e -> ExceptionDispatchInfo.raise true e 

type ResultCell<'T> internal (res : Uri) = 
    let bc = BlobCell.Get<'T option>(res)

    member __.SetResult(result : 'T) = bc.SetValue(Some result)
    member __.TryGetResult() : Async<'T option> = bc.GetValue()
    
    member __.AwaitResult() : Async<'T> = 
        async { 
            let! r = __.TryGetResult()
            match r with
            | None -> return! __.AwaitResult()
            | Some r -> return r
        }
    
    interface IResource with
        member __.Uri = res
    
    static member Get<'T>(res : Uri) = new ResultCell<'T>(res)
    static member Init<'T>(res : Uri) = 
        async { let! bc = BlobCell.Init<'T option>(res ,fun () -> None)
                return new ResultCell<'T>(res) }

    interface ISerializable with
        member x.GetObjectData(info: SerializationInfo, context: StreamingContext): unit = 
            info.AddValue("uri", res, typeof<Uri>)

    new(info: SerializationInfo, context: StreamingContext) =
        let res = info.GetValue("uri", typeof<Uri>) :?> Uri
        new ResultCell<'T>(res)


type ResultCell =
    static member GetUri(container) = uri "resultcell:%s/" container

type ResultAggregator<'T> internal (res : Uri) = 
    
    member __.SetResult(index : int, value : 'T) : Async<bool> = 
        async { 
            let e = new ResultAggregatorEntity(res.PartitionKey, index, null, ETag = "*")
            let bcu = BlobCell.GetUri(res.Container)
            let! bc = BlobCell.Init(bcu, fun () -> value)
            e.BlobCellUri <- bcu.ToString()
            let! u = Table.replace res.Table e
            let l = Latch.Get(Counter.GetUri(res.Table, res.PartitionKey))
            let! curr = l.Decrement()
            return curr = 0
        }
    
    member __.Complete = Latch.Get(Counter.GetUri(res.Table, res.PartitionKey)).Value = 0
    
    member __.ToArray() : Async<'T []> = 
        async { 
            let! xs = Table.readBatch<ResultAggregatorEntity> res.Table res.PartitionKey
            let bs = 
                xs
                |> Seq.filter (fun x -> x.RowKey <> "") // skip latch entity
                |> Seq.map (fun x -> x.BlobCellUri)
                |> Seq.map (fun x -> BlobCell.Get(new Uri(x)))
                |> Seq.toArray
            
            let re = Array.zeroCreate<'T> bs.Length
            let i = ref 0
            for b in bs do
                let! v = b.GetValue<'T>()
                re.[!i] <- v
                incr i
            return re
        }
    
    interface IResource with member __.Uri = res
    
    static member Get<'T>(res : Uri) = new ResultAggregator<'T>(res)
    static member Init<'T>(res : Uri, size : int) = 
        async { 
            let! l = Latch.Init(Counter.GetUri(res.Table, res.PartitionKey), size)
            for i = 0 to size - 1 do
                let e = new ResultAggregatorEntity(res.PartitionKey, i, "")
                do! Table.insert res.Table e
            return new ResultAggregator<'T>(res)
        }

    interface ISerializable with
        member x.GetObjectData(info: SerializationInfo, context: StreamingContext): unit = 
            info.AddValue("uri", res, typeof<Uri>)

    new(info: SerializationInfo, context: StreamingContext) =
        let res = info.GetValue("uri", typeof<Uri>) :?> Uri
        new ResultAggregator<'T>(res)

type ResultAggregator =
    static member GetUri(container, id) = uri "aggregator:%s/%s" container id
    static member GetUri(container) = Counter.GetUri(container, guid())


type ResourceFactory private () =
    member __.RequestCounter(container, count) = Counter.Init(Counter.GetUri container, count)
    member __.RequestResultAggregator<'T>(container, count : int) = ResultAggregator<'T>.Init(ResultAggregator.GetUri container, count)
    member __.RequestCancellationTokenSource(container, ?parent) = DistributedCancellationTokenSource.Init(DistributedCancellationTokenSource.GetUri container, ?parent = parent)
    member __.RequestResultCell<'T>(container) = ResultCell<Result<'T>>.Init(ResultCell.GetUri container)
          
    member __.RequestCounter(count) = Counter.Init(Counter.GetUri "tmp", count)
    member __.RequestResultAggregator<'T>(count : int) = ResultAggregator<'T>.Init(ResultAggregator.GetUri "tmp", count)
    member __.RequestCancellationTokenSource(?parent) = DistributedCancellationTokenSource.Init(DistributedCancellationTokenSource.GetUri "tmp", ?parent = parent)
    member __.RequestResultCell<'T>() = ResultCell<Result<'T>>.Init(ResultCell.GetUri "tmp")

    static member Init () = new ResourceFactory()