﻿namespace Nessos.MBrace.Runtime.Tests

    open Nessos.MBrace
    open Nessos.MBrace.Client

    open NUnit.Framework
    
    [<Category("LocalTests")>]
    type ``Local Cloud Tests`` () =
        inherit ``Cloud Tests`` ()

        override __.Name = "Local Cloud Tests"
        override __.IsLocalTesting = true

        override __.ExecuteExpression(expr : Quotations.Expr<Cloud<'T>>) : 'T =
            MBrace.RunLocal expr