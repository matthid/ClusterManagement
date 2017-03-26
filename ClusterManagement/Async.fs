namespace ClusterManagement

module Async = 
    let lift f =
        f >> async.Return
    let bind f a =
        async.Bind(a, f)
    let map f a =
        bind (lift f) a


        
[<AutoOpen>]
module AsyncExtensions =
    type internal VolatileBarrier() =
        [<VolatileField>]
        let mutable isStopped = false
        member __.Proceed = not isStopped
        member __.Stop() = isStopped <- true
    open System.Threading
    open System.Threading.Tasks
    let internal startImmediateAsTask (token:CancellationToken, computation : Async<_>, taskCreationOptions) : Task<_> =
        if obj.ReferenceEquals(computation, null) then raise <| System.NullReferenceException("computation is null!")
        let taskCreationOptions = defaultArg taskCreationOptions TaskCreationOptions.None
        let tcs = new TaskCompletionSource<_>(taskCreationOptions)

        // The contract: 
        //      a) cancellation signal should always propagate to task
        //      b) CancellationTokenSource that produced a token must not be disposed until the the task.IsComplete
        // We are:
        //      1) registering for cancellation signal here so that not to miss the signal
        //      2) disposing the registration just before setting result/exception on TaskCompletionSource -
        //              otherwise we run a chance of disposing registration on already disposed  CancellationTokenSource
        //              (See (b) above)
        //      3) ensuring if reg is disposed, we do SetResult
        let barrier = VolatileBarrier()
        let reg = token.Register(fun _ -> if barrier.Proceed then tcs.SetCanceled())
        let task = tcs.Task
        let disposeReg() =
            barrier.Stop()
            if not (task.IsCanceled) then reg.Dispose()

        let a = 
            async { 
                try
                    let! result = computation
                    do 
                        disposeReg()
                        tcs.TrySetResult(result) |> ignore
                with
                |   e -> 
                        disposeReg()
                        tcs.TrySetException(e) |> ignore
            }
        Async.StartImmediate(a, token)
        task
    type Async with
        static member StartImmediateAsTask (computation,?taskCreationOptions,?cancellationToken)=
            let token = defaultArg cancellationToken Async.DefaultCancellationToken       
            startImmediateAsTask(token,computation,taskCreationOptions)
