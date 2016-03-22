namespace Logary.Internals


/// Module that is the ONLY module allowed to have global variables; created so
/// that consumer applications may call into Logary without having a reference
/// to the LogManager first.
///
/// The globals in this module are configured in module "Logging".
module internal Globals =
  open System
  open System.Threading
  open Logary

  let private globalLock = obj ()

  /// This is the "Global Variable" containing the last configured
  /// Logary instance. If you configure more than one logary instance
  /// this will be replaced. It is internal so that noone
  /// changes it from the outside. Don't use this directly if you can
  /// avoid it, and instead take a c'tor dependency on LogaryRegistry
  /// or use IoC with a contextual lookup to resolve proper loggers.
  let private singleton : LogaryInstance option ref = ref None

  /// For use when changing global Logary values
  /// Will hold a lock on modifying Globals
  /// until disposed. In general, you should not
  /// use GlobalContext directly - use the Logging
  /// module instead
  type GlobalContext private (s) =
    // Using System.Threading.Monitor here rather than Hopac.Lock
    // as suggested in the Hopac documents for "short non-blocking
    // critical sections" 
    member x.singleton : LogaryInstance option = s
    /// Create a new global context. You MUST dispose of this
    /// once you have finished modifying the global state
    static member create () =
      Monitor.Enter globalLock
      new GlobalContext(!singleton)
    interface IDisposable with
      member x.Dispose() =
        Monitor.Exit globalLock

  /// A list of all loggers yet to be configured
  let private flyweights : FlyweightLogger list ref = ref []

  let setSingleton (ctx : GlobalContext) (li : LogaryInstance) =
    singleton := Some li

  let clearSingleton (ctx : GlobalContext) =
    singleton := None

  let addFlyweight (ctx : GlobalContext) (fwl : FlyweightLogger) =
    flyweights := fwl :: !flyweights

  /// Gives f a snapshot of the current flyweights
  let withFlyweights (ctx : GlobalContext) f =
    f !flyweights

  let clearFlyweights (ctx : GlobalContext) =
    flyweights := []