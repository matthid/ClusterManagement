namespace ClusterManagement

module Async = 
    let lift f =
        f >> async.Return
    let bind f a =
        async.Bind(a, f)
    let map f a =
        bind (lift f) a