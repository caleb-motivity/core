﻿// $begin{copyright}
//
// This file is part of WebSharper
//
// Copyright (c) 2008-2016 IntelliFactory
//
// Licensed under the Apache License, Version 2.0 (the "License"); you
// may not use this file except in compliance with the License.  You may
// obtain a copy of the License at
//
//     http://www.apache.org/licenses/LICENSE-2.0
//
// Unless required by applicable law or agreed to in writing, software
// distributed under the License is distributed on an "AS IS" BASIS,
// WITHOUT WARRANTIES OR CONDITIONS OF ANY KIND, either express or
// implied.  See the License for the specific language governing
// permissions and limitations under the License.
//
// $end{copyright}

// Main translation module from .NET forms to JavaScript
module WebSharper.Compiler.TypeTranslator

open WebSharper.Core
open WebSharper.Core.AST
open WebSharper.Core.Metadata

type LookupTypeResult =
    | Class of Address * CustomTypeInfo * option<ClassInfo>
    | Interface of InterfaceInfo                
    | Unknown

let CustomTranslations: IDictionary<TypeDefinition, list<TSType> -> TSType> =
    let (|Last|_|) l =
        match List.rev l with
        | l :: r -> Some (List.rev r, l)
        | _ -> None

    let jsTyp t =
        TypeDefinition{
            Assembly = "WebSharper.JavaScript"
            FullName = t
        }
    let coreTyp t =
        TypeDefinition{
            Assembly = "WebSharper.Core"
            FullName = t
        }
    let fscoreTyp t =
        TypeDefinition{
            Assembly = "FSharp.Core"
            FullName = t
        }
    let corlibTyp t =
        TypeDefinition{
            Assembly = "mscorlib"
            FullName = t
        }
    let inv() =
        invalidOp "unexpected type arguments for JS interop type"
    dict [
        yield jsTyp "WebSharper.JavaScript.Object`1", (List.head >> TSType.ObjectOf) 
        yield coreTyp "WebSharper.JavaScript.Optional`1", List.head
        yield corlibTyp "System.Nullable`1", List.head
        yield fscoreTyp "Microsoft.FSharp.Core.FSharpRef`1", TSType.Tuple  
        yield fscoreTyp "Microsoft.FSharp.Control.FSharpAsyncReplyChannel`1",
            function
            | [t] -> TSType.Function(None, [t], None, TSType.Void)
            | _ -> inv()
        for i = 1 to 7 do
            yield coreTyp ("WebSharper.JavaScript.Union`" + string i), TSType.Union
        yield coreTyp "WebSharper.JavaScript.FuncWithArgs`2",
            function 
            | [ TSType.ArrayOf e; r ] -> TSType.Function(None, [], Some e, r)
            | [ TSType.Tuple a; r ] -> TSType.Lambda(a, r)
            | _ -> inv()
        yield coreTyp "WebSharper.JavaScript.FuncWithThis`2",
            function
            | [ t; TSType.Function(_, a, e, r) ] -> TSType.Function(Some t, a, e, r) 
            | _ -> inv()
        yield coreTyp "WebSharper.JavaScript.FuncWithOnlyThis`2", 
            function
            | [ t; r ] -> TSType.Function(Some t, [], None, r) 
            | _ -> inv()
        yield coreTyp "WebSharper.JavaScript.FuncWithArgsRest`2", 
            function
            | [ TSType.Tuple a; e; r ] -> TSType.Function(None, a, Some e, r) 
            | _ -> inv()
        for i = 0 to 6 do
            yield coreTyp ("WebSharper.JavaScript.FuncWithRest`" + string (i + 2)), 
                function
                | Last (Last (a, e), r) -> TSType.Function(None, a, Some e, r) 
                | _ -> inv()
            yield coreTyp ("WebSharper.JavaScript.ThisAction`" + string (i + 1)), 
                function
                | t :: a -> TSType.Function(Some t, a, None, TSType.Void) 
                | _ -> inv()
            yield coreTyp ("WebSharper.JavaScript.ThisFunc`" + string (i + 2)), 
                function
                | t :: Last (a, r) -> TSType.Function(Some t, a, None, r) 
                | _ -> inv()
            yield coreTyp ("WebSharper.JavaScript.ParamsAction`" + string (i + 1)), 
                function
                | Last (a, e) -> TSType.Function(None, a, Some e, TSType.Void) 
                | _ -> inv()
            yield coreTyp ("WebSharper.JavaScript.ParamsFunc`" + string (i + 2)), 
                function
                | Last (Last (a, e), r) -> TSType.Function(None, a, Some e, r) 
                | _ -> inv()
            yield coreTyp ("WebSharper.JavaScript.ThisParamsAction`" + string (i + 2)), 
                function
                | t :: Last (a, e) -> TSType.Function(Some t, a, Some e, TSType.Void) 
                | _ -> inv()
            yield coreTyp ("WebSharper.JavaScript.ThisParamsFunc`" + string (i + 3)), 
                function
                | t :: Last (Last (a, e), r) -> TSType.Function(Some t, a, Some e, r) 
                | _ -> inv()
    ]

type TypeTranslator(lookupType: TypeDefinition -> LookupTypeResult, ?tsTypeOfAddress) =
    let defaultTsTypeOfAddress (a: Address) =
        let t = a.Address.Value |> List.rev
        match a.Module with
        | StandardLibrary
        | JavaScriptFile _
        | CurrentModule -> TSType.Named t
        | WebSharperModule m -> TSType.Importing (m, t)
        | ImportedModule m -> TSType.Imported(m, t) 

    let tsTypeOfAddress = defaultArg tsTypeOfAddress defaultTsTypeOfAddress

    let mappedTypes = Dictionary()

    member this.TSTypeOfDef(t: TypeDefinition) =
        match mappedTypes.TryGetValue t with
        | true, tt -> tt
        | _ ->
            let res =
                match lookupType t with
                | Class (a, _, Some c) ->
                    match c.Type with
                    | Some t -> t
                    | _ -> tsTypeOfAddress a
                | Class (_, DelegateInfo i, None) ->
                    TSType.Lambda(i.DelegateArgs |> List.map (this.TSTypeOf [||]), this.TSTypeOf [||] i.ReturnType)
                | Class (_, EnumInfo t, None) ->
                    this.TSTypeOfDef t
                | Class (a, (FSharpRecordInfo _ | FSharpUnionInfo _ | FSharpUnionCaseInfo _), None) ->
                    tsTypeOfAddress a
                | Interface i ->
                    tsTypeOfAddress i.Address
                | _ -> TSType.Any
            mappedTypes.Add(t, res)
            res

    member this.TSTypeOfGenParam i (p: GenericParam) =
        match p.Type with
        | Some t -> t
        | _ -> TSType.Param i

    member this.TSTypeOfConcrete (gs: GenericParam[]) (t: Concrete<TypeDefinition>) =
        let e = t.Entity
        let gen = t.Generics |> List.map (this.TSTypeOf gs)
        match CustomTranslations.TryGetValue e with
        | true, f -> 
            try f gen
            with _ ->
                failwithf "Error during translating type %O" (ConcreteType t)
        | _ ->
        let td = this.TSTypeOfDef e
        match gen with
        | [] -> td
        | _ -> 
            match td with
            | TSType.Importing _
            | TSType.Imported _
            | TSType.Named _ ->
                TSType.Generic(td, gen)
            | TSType.Function _ ->
                td.SubstituteGenerics (Array.ofList gen)
            | _ -> td

    member this.TSTypeOf (gs: GenericParam[]) (t: Type) =
        match t with 
        | ConcreteType t -> this.TSTypeOfConcrete gs t
        | ArrayType (t, a) -> 
            match a with
            | 1 -> TSType.ArrayOf (this.TSTypeOf gs t)
            | 2 -> TSType.ArrayOf (TSType.ArrayOf (this.TSTypeOf gs t))
            | _ -> failwith "only 1 and 2-dim arrays are supported"
        | TupleType (ts, _) -> TSType.Tuple (ts |> List.map (this.TSTypeOf gs))
        | FSharpFuncType (a, r) -> 
            let ta =
                match a with
                | VoidType -> []
                | _ -> [this.TSTypeOf gs a]
            TSType.Lambda(ta, this.TSTypeOf gs r)
        | ByRefType t -> TSType.Any // TODO byrefs
        | VoidType -> TSType.Void
        | TypeParameter i 
        | StaticTypeParameter i -> 
            if i >= gs.Length then TSType.Param i else this.TSTypeOfGenParam i gs.[i]
        | LocalTypeParameter -> TSType.Any
