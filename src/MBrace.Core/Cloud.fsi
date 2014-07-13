namespace Nessos.MBrace

    open System

    open Nessos.MBrace.CloudExpr

    type CloudAttribute = ReflectedDefinitionAttribute
    type ProcessId = Nessos.MBrace.CloudExpr.ProcessId

    [<Sealed>]
    /// Disable tracing for cloud workflow
    type NoTraceInfoAttribute =
        class
            inherit System.Attribute
            new : unit -> NoTraceInfoAttribute
            member Name : string
        end

    [<Sealed>]
    /// Disable [<Cloud>] warnings for current workflow
    type NoWarnAttribute =
        class
            inherit System.Attribute
            new : unit -> NoWarnAttribute
        end

    [<Sealed>]
    type Cloud<'T> =
        class
            internal new : cloudExpr:CloudExpr -> Cloud<'T>
            member Type : Type
            member internal CloudExpr : CloudExpr
        end
        
    /// Contains the methods (combinators) to express the primitive computations directly supported by
    /// the MBrace runtime.
    type Cloud =
        class

            static member inline internal wrapExpr   : cloudExpr:CloudExpr -> Cloud<'T>
            static member inline internal unwrapExpr : cloudValue:Cloud<'T> -> CloudExpr

            /// Returns a cloud computation that will execute the given computations
            /// possibly in parallel and will return when any of the supplied computations
            /// have returned a successful value or if all of them fail to succeed. 
            /// If a computation succeeds the rest of them are canceled.
            /// The success of a computation is encoded as an option type.
            /// This operator may create distribution.
            static member Choice : computations:seq<Cloud<'T option>> -> Cloud<'T option>
                
            /// Returns the ProcessId of the current process.
            static member GetProcessId : unit -> Cloud<ProcessId>

            /// Returns the taskId of the current executing context.
            static member GetTaskId : unit -> Cloud<string>
                
            /// Returns the number of worker nodes in the current runtime.
            /// This operator may create distribution.
            static member GetWorkerCount : unit -> Cloud<int>
                
            /// Writes a string to the user logs.
            static member Log : msg:string -> Cloud<unit>

            /// Writes a string to the user logs using the specified format.
            static member Logf : fmt:Printf.StringFormat<'T,Cloud<unit>> -> 'T

            /// <summary>Converts an asynchronous computation to a cloud computation.</summary>
            /// <param name="asyncComputation">The computation to be converted.</param>
            static member OfAsync : asyncComputation:Async<'T> -> Cloud<'T>
                
            /// <summary>Returns a cloud computation that will execute the given computations
            /// possibly in parallel and returns the array of their results.
            /// This operator may create distribution.
            /// If any exceptions are thrown all the results will be aggregated in an exception.</summary>
            /// <param name="computations">The computations to be executed in parallel.</param>  
            static member Parallel : computations:seq<Cloud<'T>> -> Cloud<'T []>

            /// <summary>Converts a cloud computation to a computation that will 
            /// be executed locally (on the same node).
            /// </summary>
            /// <param name="cloudComputation">The computation to be converted.</param>
            static member ToLocal : cloudComputation:Cloud<'T> -> Cloud<'T>
                
            /// <summary>Wraps a cloud computation in a computation that will return the
            /// same result but will also write trace information in the user logs.
            /// </summary>
            /// <param name="cloudComputation">The computation to be traced.</param>
            static member Trace : cloudComputation:Cloud<'T> -> Cloud<'T>
        end

    /// The monadic builder.
    type CloudBuilder =
        class
            new : unit -> CloudBuilder
            member Bind : computation:Cloud<'T> * bindF:('T -> Cloud<'U>) -> Cloud<'U>
            member Combine : first:Cloud<unit> * second:Cloud<'T> -> Cloud<'T>
            member Delay : f:(unit -> Cloud<'T>) -> Cloud<'T>
            member For : values:'T [] * bindF:('T -> Cloud<unit>) -> Cloud<unit>
            member For : values:'T list * bindF:('T -> Cloud<unit>) -> Cloud<unit>
            member Return : value:'T -> Cloud<'T>
            member ReturnFrom : computation:Cloud<'T> -> Cloud<'T>
            member TryFinally : computation:Cloud<'T> * compensation:(unit -> unit) -> Cloud<'T>
            member TryWith : computation:Cloud<'T> * exceptionF:(exn -> Cloud<'T>) -> Cloud<'T>
            member Zero : unit -> Cloud<unit>
            member Using<'T, 'U when 'T :> ICloudDisposable> : 'T * ('T -> Cloud<'U>) -> Cloud<'U> 

            [<CompilerMessage("While loops in distributed computation not recommended; consider using an accumulator pattern instead.", 44)>]
            member While : guardF:(unit -> bool) * body:Cloud<unit> -> Cloud<unit>
        end

    [<AutoOpen>]
    [<CompilationRepresentation(CompilationRepresentationFlags.ModuleSuffix)>]
    module CloudBuilder = begin

        val cloud : CloudBuilder
        val internal mkTry<'Exc,'T when 'Exc :> exn> :
            expr:Cloud<'T> -> Cloud<'T option>
    end
