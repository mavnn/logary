module Logary.Targets.Riemann

// https://github.com/aphyr/riemann-ruby-client/blob/master/lib/riemann/event.rb
// https://github.com/aphyr/riemann-java-client/tree/master/src/main/java/com

open ProtoBuf

open FSharp.Actor

open NodaTime

open System
open System.Collections.Generic
open System.IO
open System.Net
open System.Net.Sockets
open System.Net.Security
open System.Security.Cryptography.X509Certificates

open Logary
open Logary.Riemann.Client
open Logary.Riemann.Messages
open Logary.Target
open Logary.Internals
open Logary.Internals.Tcp

/// Make a stream from a TcpClient and an optional certificate validation
/// function
let private mkStream (client : TcpClient) = function
  | Some f ->
    let cb = new RemoteCertificateValidationCallback(fun _ -> f)
    new SslStream(client.GetStream(),
                  leaveInnerStreamOpen = false,
                  userCertificateValidationCallback = cb)
    :> Stream
  | None ->
    client.GetStream() :> Stream

let private mkClient (ep : IPEndPoint) =
  let c = new TcpClient(ep.Address.ToString(), ep.Port)
  c.NoDelay <- true
  c

// Note: is it possible to do 'per millisecond'?
// https://github.com/chillitom/Riemann.Net/blob/master/Riemann.Net/Event.cs#L25
let private asEpoch (i : Instant) = i.Ticks / NodaConstants.TicksPerSecond

/// Convert the LogLevel to a Riemann (service) state
let private mkState = function
  | Verbose | Debug | Info -> "ok"
  | Warn                   -> "warning"
  | Error | Fatal          -> "critical"

/// The default way of sending attributes; just do ToString() on them
let mkAttrsFromData (m : Map<string, obj>) =
  m |> Seq.map (fun kvp -> Attribute(kvp.Key, kvp.Value.ToString())) |> List.ofSeq

/// Create an Event from a LogLine, supplying a function that optionally changes
/// the event before yielding it.
let mkEventL
  hostname ttl confTags mkAttrsFromData
  ({ message       = message
     data          = data
     level         = level
     tags          = tags
     timestamp     = timestamp
     path          = path
     ``exception`` = ex } as ll) =
  Event.CreateDouble(1.,
                     asEpoch timestamp,
                     mkState level,
                     sprintf "%s.%s" path message, // path = metric name = riemann's 'service'
                     hostname,
                     "",
                     confTags |> Option.fold (fun s t -> s @ t) tags,
                     ttl,
                     mkAttrsFromData data) // TODO: make attributes from map

/// Create an Event from a Measure
let mkEventM
  hostname ttl confTags
  ({ m_path      = path
     m_timestamp = timestamp
     m_level     = level
     m_unit      = u } as m ) =
  let tags = confTags |> Option.fold (fun s t -> s @ t) []
  Event.CreateDouble(Measure.getValueFloat m, asEpoch timestamp,
                     mkState level, path.joined, hostname,
                     u.ToString(), tags, ttl, [])

// TODO: a way of discriminating between ServiceName-s.

/// The Riemann target will always use TCP in this version.
type RiemannConf =
  { /// location of the riemann server
    endpoint     : IPEndPoint

    /// A factory function for the WriteClient - useful for testing the target
    /// and for replacing the client with a high-performance client if the async
    /// actor + async + TcpClient isn't enough, but you want to try the async
    /// socket pattern.
    clientFac    : IPEndPoint -> TcpClient

    /// validation function; setting this means you need to be able to validate
    /// the certificate you get back when connecting to Riemann -- if you set
    /// this value the target will try and create a TLS connection.
    ///
    /// Parameters:
    ///  - X509Certificate certificate
    ///  - X509Chain chain
    ///  - SslPolicyErrors sslPolicyErrors
    ///
    /// Returns: bool indicating whether to accept the cert or not
    caValidation : (X509Certificate -> X509Chain -> SslPolicyErrors -> bool) option

    /// An optional mapping function that can change the Event that is generated by
    /// default.
    fLogLine     : LogLine -> Event

    /// An optional mapping function that can change the Event that is generated by
    /// default.
    fMeasure     : Measure -> Event

    /// The hostname to send to riemann
    hostname     : string

    /// An optional list of tags to apply to everything sent to riemann
    tags         : string list option }
  /// Creates a new Riemann target configuration
  static member Create(?endpoint : IPEndPoint, ?clientFac, ?caValidation,
                       ?fLogLine, ?fMeasure, ?hostname, ?ttl, ?tags) =
    let ttl        = defaultArg ttl 10.f
    let hostname   = defaultArg hostname (Dns.GetHostName())
    { endpoint     = defaultArg endpoint (IPEndPoint(IPAddress.Loopback, 5555))
      clientFac    = defaultArg clientFac mkClient
      caValidation = defaultArg caValidation None
      fLogLine     = defaultArg fLogLine (mkEventL hostname ttl tags mkAttrsFromData)
      fMeasure     = defaultArg fMeasure (mkEventM hostname ttl tags)
      hostname     = hostname
      tags         = tags }
  static member Default = RiemannConf.Create()

type private RiemannTargetState =
  { client : TcpClient
    stream : Stream }

// To Consider: could be useful to spawn multiple of this one: each is async and implement
// an easy way to send/recv -- multiple will allow interleaving of said requests

// To Consider: sending multiple events to this

// So currently we're in push mode; did a Guage, Histogram or other thing send
// us this metric? Or are Logary 'more dump' and simply shovel the more simple
// counters and measurements (e.g. function execution timing) to Riemann
// so that riemann can make up its own data?
//
// See https://github.com/aphyr/riemann-java-client/blob/master/src/main/java/com/codahale/metrics/riemann/RiemannReporter.java#L282
// https://github.com/aphyr/riemann-ruby-client/blob/master/lib/riemann/client/tcp.rb

let riemannLoop (conf : RiemannConf) metadata =
  (fun (inbox : IActor<_>) ->
    let rec init () =
      async {
        let client = conf.clientFac conf.endpoint
        let stream = mkStream client conf.caValidation
        return! running { client = client
                          stream = stream } }

    and running state =
      async {
        let! msg, mopt = inbox.Receive()
        // The server will accept a repeated list of Events, and respond
        // with a confirmation message with either an acknowledgement or an error.
        // Check the `ok` boolean in the message; if false, message.error will
        // be a descriptive string.
        match msg with
        | Log l ->
          let evt = conf.fLogLine l
          let! res = [ evt ] |> sendEvents state.stream
          match res with
          | Choice1Of2 () -> return! running state
          | Choice2Of2 err -> raise <| Exception(sprintf "server error: %s" err)
        | Measure msr ->
          let evt = conf.fMeasure msr
          let! res = [ evt ] |> sendEvents state.stream
          match res with
          | Choice1Of2 () -> return! running state
          | Choice2Of2 err -> raise <| Exception(sprintf "server error: %s" err)
        | Flush chan ->
          chan.Reply Ack
          return! running state
        | Shutdown ackChan ->
          return! shutdown state ackChan }

    and shutdown state ackChan =
      async {
        Try.safe "riemann target disposing tcp stream, then client" metadata.logger <| fun () ->
          (state.stream :> IDisposable).Dispose()
          (state.client :> IDisposable).Dispose()
        ackChan.Reply Ack
        return () }
    init ())

/// Create a new Riemann target
let create conf = TargetUtils.stdNamedTarget (riemannLoop conf)

/// C# interop
[<CompiledName("Create")>]
let create' (conf, name) = create conf name

/// Use with LogaryFactory.New( s => s.Target<Riemann.Builder>() )
type Builder(conf, callParent : FactoryApi.ParentCallback<Builder>) =

  member x.Endpoint(ep : IPEndPoint) =
    Builder({ conf with endpoint = ep }, callParent)

  member x.ClientFactory(fac : Func<IPEndPoint, TcpClient>) =
    Builder({ conf with clientFac = fun ep -> fac.Invoke ep }, callParent)

  member x.Done() =
    ! ( callParent x )

  new(callParent : FactoryApi.ParentCallback<_>) =
    Builder(RiemannConf.Default, callParent)

  interface Logary.Target.FactoryApi.SpecificTargetConf with
    member x.Build name = create conf name
