module Logary.Obnoxious

open Logary
open Logary.Configuration
open Logary.Targets
open Hopac
open Hopac.Infixes
open Hopac.Job.Global
open Hopac.Extensions.Async
open Hopac.Extensions.Async.Global
open System
open System.Collections.Concurrent

let q = ConcurrentQueue()

module SyncLibrary =
  let logger = Logging.getLoggerByName "Sync.library"
  let syncMethod i =
    printfn "print before"
    Message.eventInfo "blocking" |> Logger.log logger |> run
    printfn "print after"
    q.Enqueue i

module MyAsyncCode =
  let logger = Logging.getLoggerByName "Async.code"
  let doStuff i =
    Message.eventDebug "before doStuff"
    |> Message.setField "i" i
    |> Logger.log logger
    >>- (fun _ -> SyncLibrary.syncMethod i)
    >>= (fun _ -> Message.eventWarn "after doStuff" |> Message.setField "i" i |> Logger.log logger)

let doesBlock1 () =
  // This runs
  MyAsyncCode.doStuff 1 |> run

  // Blocks here
  [for i in 3..3 ->    [MyAsyncCode.doStuff i ]]
  |> List.concat
  |> Job.conIgnore
  |> run

let doesBlock2 () =
  // Blocks immediately
  [for i in 3..3 ->    [MyAsyncCode.doStuff i ]]
  |> List.concat
  |> Job.conIgnore
  |> run

let doesNotBlock1 () =
  MyAsyncCode.doStuff 1 |> run
  // Running this a second time means that...
  MyAsyncCode.doStuff 2 |> run

  // ...this doesn't block!?
  [for i in 3..3 ->    [MyAsyncCode.doStuff i ]]
  |> List.concat
  |> Job.conIgnore
  |> run

let doesNotBlock2 () =
  // Doesn't block... 
  for i in 1..10 do
    MyAsyncCode.doStuff i |> run

[<EntryPoint>]
let main argv = 
  withLogaryManager "Obnoxious" (
    withTargets [Console.create Console.empty <| PointName.ofSingle "console"]
    >> withRules [Rule.createForTarget (PointName.ofSingle "console")]
  ) |> Job.Ignore |> run

//  doesBlock1 ()
//  doesBlock2 ()
//  doesNotBlock1 ()
  doesNotBlock2 ()

  Async.Sleep 500 |> Async.RunSynchronously
  printfn "count %d" q.Count
  0 // return an integer exit code
