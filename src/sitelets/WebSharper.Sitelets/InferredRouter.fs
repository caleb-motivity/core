﻿// $begin{copyright}
//
// This file is part of WebSharper
//
// Copyright (c) 2008-2014 IntelliFactory
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

namespace WebSharper.Sitelets

open WebSharper
open WebSharper.JavaScript
open WebSharper.JQuery
open System.Collections.Generic
open System.Text

module internal ServerInferredOperators =

    let emptyParams = Http.ParameterCollection(Seq.empty)

    type ParseResult =
        | StrictMode
        | NoErrors
        | InvalidMethod of string
        | InvalidJson
        | MissingQueryParameter of string
        | MissingFormData of string

    type MRoute =
        {
            mutable Segments : list<string>
            QueryArgs : Http.ParameterCollection
            FormData : Http.ParameterCollection
            Method : option<string> 
            Body : option<string>
            mutable Result: ParseResult 
        }
    
        static member Empty =
            {
                Segments = []
                QueryArgs = emptyParams
                FormData = emptyParams
                Method = None
                Body = None
                Result = StrictMode
            }

        static member OfPath(path: Route) =
            {
                Segments = path.Segments
                QueryArgs = Http.ParameterCollection(path.QueryArgs |> Map.toSeq)
                FormData = emptyParams
                Method = path.Method
                Body = path.Body
                Result = StrictMode
            }

        member this.ToPath() =
            {
                Segments = this.Segments
                QueryArgs = this.QueryArgs.ToList() |> Map.ofList
                FormData = this.FormData.ToList() |> Map.ofList
                Method = this.Method
                Body = this.Body
            } : Route

        static member FromWSRequest(r: Http.Request) =
            let u = r.Uri
            let p =
                if u.IsAbsoluteUri then 
                    u.AbsolutePath 
                else 
                    let s = u.OriginalString
                    match s.IndexOf('?') with
                    | -1 -> s
                    | q -> s.Substring(0, q)
            {
                Segments = p.Split([| '/' |], System.StringSplitOptions.RemoveEmptyEntries) |> List.ofArray
                QueryArgs = r.Get
                FormData = r.Post
                Method = Some (r.Method.ToString())
                Body =
                    let i = r.Body 
                    if not (isNull i) then 
                        // We need to copy the stream because else StreamReader would close it.
                        use m =
                            if i.CanSeek then
                                new System.IO.MemoryStream(int i.Length)
                            else
                                new System.IO.MemoryStream()
                        i.CopyTo m
                        if i.CanSeek then
                            i.Seek(0L, System.IO.SeekOrigin.Begin) |> ignore
                        m.Seek(0L, System.IO.SeekOrigin.Begin) |> ignore
                        use reader = new System.IO.StreamReader(m)
                        Some (reader.ReadToEnd())
                    else None
                Result = StrictMode
            }

    type PathWriter =
        {
            mutable AddSlash : bool
            PathWriter : StringBuilder
            mutable QueryWriter : StringBuilder
        }

        static member New(startWithSlash) =
            let b = StringBuilder 128
            if startWithSlash then 
                b.Append('/') |> ignore
            {
                AddSlash = false
                PathWriter = b
                QueryWriter = null
            }

        member this.NextSegment() =
            if this.AddSlash then 
                this.PathWriter.Append('/')
            else 
                this.AddSlash <- true
                this.PathWriter

        member this.ToPath() =
            {
                Segments = [ this.PathWriter.ToString() ]
                QueryArgs = 
                    let q = this.QueryWriter
                    if isNull q then Map.empty else Route.ParseQuery (q.ToString())
                FormData = Map.empty
                Method = None
                Body = None
            }

        member this.ToLink() =
            let p = this.PathWriter
            let q = this.QueryWriter
            if not (isNull q) then
                p.Append('?').Append(q.ToString()) |> ignore
            p.ToString()

    type InferredRouter =
        {
            IParse : MRoute -> obj option
            IWrite : PathWriter * obj -> unit 
        }   

        member this.Link(action: 'T) =
            let w = PathWriter.New(true)
            this.IWrite(w, box action)
            w.ToLink()

        member this.Parse<'T>(path) =
            match this.IParse(path) with
            | Some v ->
                if List.isEmpty path.Segments then Some (unbox<'T> v) else None
            | None -> None

    open RouterOperators

    let IEmpty : InferredRouter =
        {
            IParse = fun _ -> None
            IWrite = ignore
        }

    let internal iString : InferredRouter =
        {
            IParse = fun path ->
                match path.Segments with
                | h :: t -> 
                    path.Segments <- t
                    Some (decodeURIComponent h |> box)
                | _ -> None
            IWrite = fun (w, value) ->
                if isNull value then 
                    w.NextSegment().Append("null") |> ignore
                else
                    w.NextSegment().Append(encodeURIComponent (unbox value)) |> ignore
        }

    let internal iChar : InferredRouter =
        {
            IParse = fun path ->
                match path.Segments with
                | h :: t when h.Length = 1 -> 
                    path.Segments <- t
                    Some (char (decodeURIComponent h) |> box)
                | _ -> None
            IWrite = fun (w, value) ->
                w.NextSegment().Append(encodeURIComponent (string value)) |> ignore
        }

    let inline iTryParse< ^T when ^T: (static member TryParse: string * byref< ^T> -> bool) and ^T: equality>() =
        {
            IParse = fun path ->
                match path.Segments with
                | h :: t -> 
                    let mutable res = Unchecked.defaultof< ^T>
                    let ok = (^T: (static member TryParse: string * byref< ^T> -> bool) (h, &res))
                    if ok then 
                        path.Segments <- t
                        Some (box res)
                    else None
                | _ -> None
            IWrite = fun (w, value) ->
                w.NextSegment().Append(value) |> ignore
        }

    let iGuid = iTryParse<System.Guid>()
    let iInt = iTryParse<int>()
    let iDouble = iTryParse<double>()
    let iSByte = iTryParse<sbyte>() 
    let iByte = iTryParse<byte>() 
    let iInt16 = iTryParse<int16>() 
    let iUInt16 = iTryParse<uint16>() 
    let iUInt = iTryParse<uint32>() 
    let iInt64 = iTryParse<int64>() 
    let iUInt64 = iTryParse<uint64>() 
    let iSingle = iTryParse<single>() 

    let internal iBool : InferredRouter =
        {
            IParse = fun path ->
                match path.Segments with
                | h :: t -> 
                    match System.Boolean.TryParse h with
                    | true, g ->
                        path.Segments <- t
                        Some (box g)
                    | _ -> None
                | _ -> None
            IWrite = fun (w, value) ->
                w.NextSegment().Append(if value :?> bool then "True" else "False") |> ignore
        }

    let iDateTime format =
        let format = defaultArg format "yyyy-MM-dd-HH.mm.ss"
        {
            IParse = fun path ->
                match path.Segments with
                | h :: t -> 
                    match System.DateTime.TryParseExact(h, format, System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.RoundtripKind) with
                    | true, d ->
                        path.Segments <- t
                        Some (box d)
                    | _ -> None
                | _ -> None
            IWrite = fun (w, value) ->                
                w.NextSegment().Append((value:?> System.DateTime).ToString(format)) |> ignore
        }

    let iWildcardString = 
        {
            IParse = fun path ->
                let s = path.Segments |> String.concat "/"
                path.Segments <- []
                Some (box s)
            IWrite = fun (w, value) ->
                w.NextSegment().Append(value) |> ignore
        }

    let iWildcardArray (itemType: System.Type) (item: InferredRouter) = 
        {
            IParse = fun path ->
                let acc = ResizeArray()
                let origSegments = path.Segments
                let rec loop() =
                    match path.Segments with
                    | [] -> 
                        let arr = System.Array.CreateInstance(itemType, acc.Count)
                        for i = 0 to acc.Count - 1 do
                            arr.SetValue(acc.[i], i)
                        Some (box arr)
                    | _ -> 
                        match item.IParse(path) with
                        | Some o ->
                            acc.Add(o)
                            loop()
                        | None -> 
                            path.Segments <- origSegments
                            None
                loop()
            IWrite = fun (w, value) ->
                let arr = value :?> System.Array
                let l = arr.Length 
                for i = 0 to l - 1 do
                    item.IWrite (w, arr.GetValue i) 
        }

    let IMap (decode: obj -> obj) (encode: obj -> obj) router =
        {
            IParse = fun path ->
                router.IParse path |> Option.map decode
            IWrite = fun (w, value) ->
                router.IWrite(w, encode value)
        }

    let iWildcardList (itemType: System.Type) (item: InferredRouter) : InferredRouter = 
        let converter = 
            System.Activator.CreateInstance(typedefof<Router.ListArrayConverter<_>>.MakeGenericType(itemType))
            :?> Router.IListArrayConverter
        iWildcardArray itemType item |> IMap converter.OfArray converter.ToArray

    let INullable (item: InferredRouter) : InferredRouter =
        {
            IParse = fun path ->
                match path.Segments with
                | "null" :: p -> 
                    path.Segments <- p
                    Some null
                | _ ->
                    item.IParse path
            IWrite = fun (w, value) ->
                if isNull value then 
                    w.NextSegment().Append("null") |> ignore
                else
                    item.IWrite(w, value)
        }

    let error<'T> err path =
        match path.Result with
        | StrictMode -> None
        | _ ->
            path.Result <- err
            Some (Unchecked.defaultof<'T>)
        
    type BF = System.Reflection.BindingFlags
    let flags = BF.Public ||| BF.NonPublic ||| BF.Static ||| BF.Instance
    
    let IWithCustomErrors (typ: System.Type) (item: InferredRouter) =
        let cases = Reflection.FSharpType.GetUnionCases(typ, flags)
        let caseCtors = 
            cases |> Array.map (fun c -> Reflection.FSharpValue.PreComputeUnionConstructor(c, flags))
        let decodeResultAction =
            typ.GetProperty("Action", flags).GetGetMethod(true)
        {
            IParse = fun path ->
                let currentResult = path.Result
                path.Result <- NoErrors 
                match item.IParse path with
                | None ->
                    path.Result <- currentResult
                    None
                | Some v ->
                    let innerResult = path.Result
                    path.Result <- currentResult 
                    match innerResult with
                    | InvalidMethod m -> Some (caseCtors.[1] [| v; m |])    
                    | InvalidJson -> Some (caseCtors.[2] [| v |])    
                    | MissingQueryParameter k -> Some (caseCtors.[3] [| v; k |])    
                    | MissingFormData k -> Some (caseCtors.[4] [| v; k |])    
                    | _ -> Some (caseCtors.[0] [| v |])    
            IWrite = fun (w, value) ->
                item.IWrite (w, decodeResultAction.Invoke(value, [||]))
        }

    let IQuery key (item: InferredRouter) : InferredRouter =
        {
            IParse = fun path ->
                match path.QueryArgs.[key] with
                | None -> error (MissingQueryParameter key) path 
                | Some q ->
                    item.IParse { MRoute.Empty with Segments = [ q ] }
            IWrite = fun (w, value) ->
                let q = 
                    match w.QueryWriter with
                    | null ->
                        let q = StringBuilder 128
                        w.QueryWriter <- q
                        q
                    | q -> q.Append('&')
                w.QueryWriter.Append(key).Append('=') |> ignore
                let qw = { w with PathWriter = q; AddSlash = false }
                item.IWrite (qw, value)
        }

    type IOptionConverter =
        abstract Get: obj -> obj option
        abstract Some: obj -> obj

    type OptionConverter<'T>() =
        interface IOptionConverter with
            member this.Get (o: obj) = unbox<'T option> o |> Option.map box
            member this.Some (x: obj) = Some (unbox<'T> x) |> box

    let IQueryOption (itemType: System.Type) key (item: InferredRouter) : InferredRouter =
        let converter = 
            System.Activator.CreateInstance(typedefof<OptionConverter<_>>.MakeGenericType(itemType))
            :?> IOptionConverter
        {
            IParse = fun path ->
                match path.QueryArgs.[key] with
                | None -> Some null
                | Some q ->
                    item.IParse { MRoute.Empty with Segments = [ q ] }
                    |> Option.map (fun v ->
                        converter.Some v
                    )
            IWrite = fun (w, value) ->
                match converter.Get value with
                | None -> ()
                | Some v ->
                let q = 
                    match w.QueryWriter with
                    | null ->
                        let q = StringBuilder 128
                        w.QueryWriter <- q
                        q
                    | q -> q.Append('&')
                w.QueryWriter.Append(key).Append('=') |> ignore
                let qw = { w with PathWriter = q; AddSlash = false }
                item.IWrite (qw, v)
        }

    let IQueryNullable key (item: InferredRouter) : InferredRouter =
        {
            IParse = fun path ->
                match path.QueryArgs.[key] with
                | None -> Some null
                | Some q ->
                    item.IParse { MRoute.Empty with Segments = [ q ] }
            IWrite = fun (w, value) ->
                match value with
                | null -> ()
                | v ->
                let q = 
                    match w.QueryWriter with
                    | null ->
                        let q = StringBuilder 128
                        w.QueryWriter <- q
                        q
                    | q -> q.Append('&')
                w.QueryWriter.Append(key).Append('=') |> ignore
                let qw = { w with PathWriter = q; AddSlash = false }
                item.IWrite (qw, v)
        }

    let Unbox<'A when 'A: equality> (router: InferredRouter) : Router<'A> =
        {
            Parse = fun path ->
                let MRoute = MRoute.OfPath(path)
                match router.IParse MRoute with
                | Some v -> Seq.singleton (MRoute.ToPath(), unbox v)
                | _ -> Seq.empty
            Write = fun value ->
                let w = PathWriter.New(false)
                router.IWrite(w, box value)
                w.ToPath() |> Seq.singleton |> Some
        }

    let IUnbox<'A when 'A: equality> (router: InferredRouter) : IRouter<'A> =
        { new IRouter<'A> with
            member this.Route req =
                let path = MRoute.FromWSRequest req
                router.Parse path
                |> Option.map unbox<'A>

            member this.Link e =
                let w = PathWriter.New(true)
                router.IWrite(w, box e)
                Some (System.Uri(w.ToLink(), System.UriKind.Relative))
        }

    let IJson<'T when 'T: equality> : InferredRouter =
        {
            IParse = fun path ->                
                match path.Body |> Option.bind (fun s -> try Some (Json.Deserialize<'T> s |> box) with _ -> None) with
                | None -> error InvalidJson path
                | Some v -> Some v
            IWrite = ignore
        }

    let IFormData (item: InferredRouter) : InferredRouter =
        {
            IParse = fun path ->
                let res = item.IParse { path with QueryArgs = path.FormData }
                match path.Result with
                | MissingQueryParameter k -> path.Result <- MissingFormData k
                | _ -> ()
                res
            IWrite = ignore
        }

    let internal ITuple (readItems: obj -> obj[]) (createTuple: obj[] -> obj) (items: InferredRouter[]) =
        let itemsList = List.ofArray items
        let l = items.Length
        {
            IParse = fun path ->
                let arr = Array.zeroCreate l 
                let origSegments = path.Segments
                let rec collect i elems =
                    match elems with 
                    | [] -> Some (createTuple arr)
                    | h :: t -> 
                        match h.IParse path with
                        | Some a -> 
                            arr.[i] <- a
                            collect (i + 1) t
                        | _ -> 
                            path.Segments <- origSegments
                            None
                collect 0 itemsList
            IWrite = fun (w, value) ->
                let values = readItems value
                for i = 0 to items.Length - 1 do
                    items.[i].IWrite (w, values.[i]) 
        }

    let internal IRecord (readFields: obj -> obj[]) (createRecord: obj[] -> obj) (fields: InferredRouter[]) =
        let fieldsList =  List.ofArray fields        
        let l = fields.Length
        {
            IParse = fun path ->
                let arr = Array.zeroCreate l
                let origSegments = path.Segments
                let rec collect i fields =
                    match fields with 
                    | [] -> Some (createRecord arr)
                    | h :: t -> 
                        match h.IParse path with
                        | Some a -> 
                            arr.[i] <- a
                            collect (i + 1) t
                        | None -> 
                            path.Segments <- origSegments
                            None
                collect 0 fieldsList
            IWrite = fun (w, value) ->
                (readFields value, fields) ||> Array.iter2 (fun v r ->
                    r.IWrite(w, v)
                )
        }

    let IDelayed (getRouter: unit -> InferredRouter) : InferredRouter =
        let r = lazy getRouter()
        {
            IParse = fun path -> r.Value.IParse path
            IWrite = fun (w, value) -> r.Value.IWrite(w, value)
        }

    let internal IArray (itemType: System.Type) (item: InferredRouter) : InferredRouter =
        {
            IParse = fun path ->
                match path.Segments with
                | h :: t -> 
                    match System.Int32.TryParse h with
                    | true, l ->
                        let arr = System.Array.CreateInstance(itemType, l)
                        let origSegments = path.Segments
                        let rec collect i =
                            if i = l then 
                                Some (box arr)
                            else 
                                match item.IParse path with 
                                | Some a -> 
                                    arr.SetValue(a, i)
                                    collect (i + 1)
                                | None ->
                                    path.Segments <- origSegments
                                    None
                        path.Segments <- t
                        collect 0
                    | _ -> None
                | _ -> None
            IWrite = fun (w, value) ->
                let arr = value :?> System.Array
                let l = arr.Length 
                w.NextSegment().Append(arr.Length) |> ignore
                for i = 0 to l - 1 do
                    item.IWrite (w, arr.GetValue i) 
        }

    let IList (itemType: System.Type) (item: InferredRouter) : InferredRouter = 
        let converter = 
            System.Activator.CreateInstance(typedefof<Router.ListArrayConverter<_>>.MakeGenericType(itemType))
            :?> Router.IListArrayConverter
        IArray itemType item |> IMap converter.OfArray converter.ToArray

    let internal IUnion getTag (caseReaders: _[]) (caseCtors: _[]) (cases: (option<string> * string[] * InferredRouter[])[]) : InferredRouter =
        let lookupCases =
            cases |> Seq.mapi (fun i (m, s, fields) -> 
                let fieldList = List.ofArray fields
                let l = fields.Length
                let parseFields p path =
                    let arr = Array.zeroCreate l
                    let origSegments = path.Segments
                    let rec collect j f =
                        match f with 
                        | [] -> 
                            Some (caseCtors.[i] arr)
                        | h :: t -> 
                            match h.IParse path with
                            | Some a -> 
                                arr.[j] <- a
                                collect (j + 1) t
                            | None -> 
                                path.Segments <- origSegments
                                None
                    path.Segments <- p
                    collect 0 fieldList
                let s = List.ofArray s
                m,
                match s with
                | [] -> 
                    "",
                    match fieldList with
                    | [] ->
                        let c = caseCtors.[i] [||]
                        -1,
                        fun p path -> Some c
                    | _ ->
                        fieldList.Length - 1, parseFields
                | [ h ] ->
                    h, 
                    match fieldList with
                    | [] ->
                        let c = caseCtors.[i] [||]
                        0,
                        fun p path -> 
                            path.Segments <- p
                            Some c
                    | _ ->
                        fieldList.Length, parseFields
                | h :: t ->
                    h, 
                    match fieldList with
                    | [] ->
                        let c = caseCtors.[i] [||]
                        t.Length,
                        fun p path -> 
                            path.Segments <- p
                            Some c
                    | _ ->
                        t.Length + fieldList.Length,
                        fun p path ->
                            match p |> List.startsWith t with
                            | Some p -> parseFields p path
                            | None -> None
            ) 
            // group by method
            |> Seq.groupBy fst |> Seq.map (fun (m, mcases) ->
                m,
                mcases |> Seq.map snd |> Seq.groupBy fst
                |> Seq.map (fun (h, hcases) ->
                    h, 
                    match hcases |> Seq.map snd |> List.ofSeq with
                    | [ _, parse ] -> parse
                    | parsers ->
                        // this is just an approximation, start with longer parsers
                        let parsers = parsers |> Seq.sortByDescending fst |> Seq.map snd |> Array.ofSeq 
                        fun p path ->
                            parsers |> Array.tryPick (fun parse -> parse p path)                        
                )
                |> dict 
            )
            |> dict
        let writeCases =
            cases |> Array.map (fun (_, s, fields) -> 
                String.concat "/" s, fields
            )
        let parse =
            match lookupCases.TryGetValue(None) with
            | true, lookup when lookupCases.Count = 1 -> 
                // no union case specifies a method
                fun path ->
                    match path.Segments with
                    | [] -> 
                        match lookup.TryGetValue("") with
                        | true, parse -> parse [] path
                        | _ -> None
                    | h :: t ->
                        match lookup.TryGetValue(h) with
                        | true, parse -> parse t path
                        | _ -> None
            | _ ->
                // some union case specifies a method
                let ignoreMethodLookup =
                    match lookupCases.TryGetValue(None) with
                    | true, lookup -> lookup
                    | _ -> dict []
                let wrongMethodLookup = 
                    lookupCases |> Seq.collect (fun (KeyValue(m, l)) ->
                        if Option.isSome m then 
                            l |> Seq.map (fun (KeyValue(h, p)) -> h, p)
                        else Seq.empty
                    ) |> Seq.groupBy fst |> Seq.map (fun (h, ps) ->
                        match ps |> Seq.map snd |> Array.ofSeq with
                        | [| p |] -> h, p
                        | parsers -> 
                            h,
                            fun p path ->
                                parsers |> Array.tryPick (fun parse -> parse p path)                        
                    ) |> dict
                fun path ->
                    let notFound() =
                        match path.Result with
                        | StrictMode -> None
                        | _ -> 
                            let res =
                                match path.Segments with
                                | [] -> 
                                    match wrongMethodLookup.TryGetValue("") with
                                    | true, parse -> parse [] path
                                    | _ -> None
                                | h :: t ->
                                    match wrongMethodLookup.TryGetValue(h) with
                                    | true, parse -> parse t path
                                    | _ -> None
                            if Option.isSome res then path.Result <- InvalidMethod path.Method.Value 
                            res
                    let explicit =
                        match lookupCases.TryGetValue(path.Method) with
                        | true, lookup -> 
                            match path.Segments with
                            | [] -> 
                                match lookup.TryGetValue("") with
                                | true, parse -> parse [] path
                                | _ -> notFound()
                            | h :: t ->
                                match lookup.TryGetValue(h) with
                                | true, parse -> parse t path
                                | _ -> notFound()
                        | _ -> notFound()
                    if Option.isSome explicit then explicit else
                    // not found with explicit method, fall back to cases ignoring method
                    match path.Segments with
                    | [] -> 
                        match ignoreMethodLookup.TryGetValue("") with
                        | true, parse -> parse [] path
                        | _ -> notFound()
                    | h :: t ->
                        match ignoreMethodLookup.TryGetValue(h) with
                        | true, parse -> parse t path
                        | _ -> notFound()
        {
            IParse = parse
            IWrite = fun (w, value) ->
                let tag = getTag value
                let path, fields = writeCases.[tag]
                if path <> "" then
                    w.NextSegment().Append(path) |> ignore
                match fields with
                | [||] -> ()
                | _ ->
                    let values = caseReaders.[tag] value : _[]
                    for i = 0 to fields.Length - 1 do
                        fields.[i].IWrite (w, values.[i]) 
        }

    let internal IClass (readFields: obj -> obj[]) (createObject: obj[] -> obj) (partsAndFields: Choice<string, InferredRouter>[]) (subClasses: (System.Type * InferredRouter)[]) =
        let partsAndFieldsList =  List.ofArray partsAndFields        
        let l = partsAndFields |> Seq.where (function Choice2Of2 _ -> true | _ -> false) |> Seq.length
        let thisClass =
            {
                IParse = fun path ->
                    let arr = Array.zeroCreate l
                    let origSegments = path.Segments
                    let rec collect i fields =
                        match fields with 
                        | [] -> Some (createObject arr)
                        | Choice1Of2 p :: t -> 
                            match path.Segments with
                            | pp :: pr when pp = p ->
                                path.Segments <- pr
                                collect i t
                            | _ ->
                                path.Segments <- origSegments
                                None
                        | Choice2Of2 h :: t -> 
                            match h.IParse path with
                            | Some a ->
                                arr.[i] <- a
                                collect (i + 1) t
                            | _ ->
                                path.Segments <- origSegments
                                None
                    collect 0 partsAndFieldsList
                IWrite = fun (w, value) ->
                    let fields = readFields value
                    let mutable index = -1
                    partsAndFields |> Array.iter (function
                        | Choice1Of2 p -> w.NextSegment().Append(p) |> ignore
                        | Choice2Of2 r ->
                            index <- index + 1
                            r.IWrite(w, fields.[index])
                    )
            }
        if Array.isEmpty subClasses then
            thisClass
        else
            { thisClass with
                IWrite = fun (w, value) ->
                    let t = value.GetType()
                    let sub = subClasses |> Array.tryPick (fun (st, sr) -> if st = t then Some sr else None)
                    match sub with 
                    | Some s -> s.IWrite (w, value)
                    | _ -> thisClass.IWrite (w, value)
            }