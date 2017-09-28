﻿// Copyright (c) Microsoft.  All Rights Reserved.  Licensed under the Apache License, Version 2.0.  See License.txt in the project root for license information.

using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Diagnostics;
using Microsoft.CodeAnalysis.CSharp.Symbols;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.PooledObjects;
using Microsoft.CodeAnalysis.Text;
using Roslyn.Utilities;

namespace Microsoft.CodeAnalysis.CSharp
{
    internal partial class Binder
    {
        /// <summary>
        /// For the purpose of escape verification we operate with the depth of local scopes.
        /// The depth is a uint, with smaller number representing shallower/wider scopes.
        /// The 0 and 1 are special scopes - 
        /// 0 is the "external" or "return" scope that is outside of the containing method/lambda. 
        ///   If something can escape to scope 0, it can escape to any scope in a given method or can be returned.
        /// 1 is the "parameter" or "top" scope that is just inside the containing method/lambda. 
        ///   If something can escape to scope 1, it can escape to any scope in a given method, but cannot be returned.
        /// n + 1 corresponds to scopes immediately inside a scope of depth n. 
        ///   Since sibling scopes do not intersect and a value cannot escape from one to another without 
        ///   escaping to a wider scope, we can use simple depth numbering without ambiguity.
        /// </summary>
        internal const uint ExternalScope = 0;
        internal const uint TopLevelScope = 1;

        // Some value kinds are semantically the same and the only distinction is how errors are reported
        // for those purposes we reserve lowest 2 bits
        private const int ValueKindInsignificantBits = 2;
        private const BindValueKind ValueKindSignificantBitsMask = unchecked((BindValueKind)~((1 << ValueKindInsignificantBits) - 1));

        /// <summary>
        /// Expression capabilities and requirements.
        /// </summary>
        [Flags]
        internal enum BindValueKind : byte
        {
            ///////////////////
            // All expressions can be classified according to the following 4 capabilities:
            //

            /// <summary>
            /// Expression can be an RHS of an assignment operation.
            /// </summary>
            /// <remarks>
            /// The following are rvalues: values, variables, null literals, properties
            /// and indexers with getters, events. 
            /// 
            /// The following are not rvalues:
            /// namespaces, types, method groups, anonymous functions.
            /// </remarks>
            RValue = 1 << ValueKindInsignificantBits,

            /// <summary>
            /// Expression can be the LHS of a simple assignment operation.
            /// Example: 
            ///   property with a setter
            /// </summary>
            Assignable = 2 << ValueKindInsignificantBits,

            /// <summary>
            /// Expression represents a location. Often referred as a "variable"
            /// Examples:
            ///  local variable, parameter, field
            /// </summary>
            RefersToLocation = 4 << ValueKindInsignificantBits,

            ///////////////////
            // The rest are just combinations of the above.
            //

            /// <summary>
            /// Expression is the RHS of an assignment operation
            /// and may be a method group.
            /// Basically an RValue, but could be treated differently for the purpose of error reporting
            /// </summary>
            RValueOrMethodGroup = RValue + 1,

            /// <summary>
            /// Expression can be an LHS of a compound assignment
            /// operation (such as +=).
            /// </summary>
            CompoundAssignment = RValue | Assignable,

            /// <summary>
            /// Expression can be the operand of an increment or decrement operation.
            /// Same as CompoundAssignment, the distinction is really just for error reporting.
            /// </summary>
            IncrementDecrement = CompoundAssignment + 1,

            /// <summary>
            /// Expression is a r/o reference.
            /// </summary>
            ReadonlyRef = RefersToLocation | RValue,

            /// <summary>
            /// Expression is passed as a ref or out parameter or assigned to a byref variable.
            /// </summary>
            RefOrOut = RefersToLocation | RValue | Assignable,

            /// <summary>
            /// Expression can be the operand of an address-of operation (&amp;).
            /// Same as RefOrOut. The difference is just for error reporting.
            /// </summary>
            AddressOf = RefOrOut + 1,

            /// <summary>
            /// Expression is the receiver of a fixed buffer field access
            /// Same as RefOrOut. The difference is just for error reporting.
            /// </summary>
            FixedReceiver = RefOrOut + 2,

            /// <summary>
            /// Expression is returned by an ordinary r/w reference.
            /// Same as RefOrOut. The difference is just for error reporting.
            /// </summary>
            RefReturn = RefOrOut + 3,
        }

        private static bool RequiresRValueOnly(BindValueKind kind)
        {
            return (kind & ValueKindSignificantBitsMask) == BindValueKind.RValue;
        }

        private static bool RequiresAssignmentOnly(BindValueKind kind)
        {
            return (kind & ValueKindSignificantBitsMask) == BindValueKind.Assignable;
        }

        private static bool RequiresVariable(BindValueKind kind)
        {
            return !RequiresRValueOnly(kind);
        }

        private static bool RequiresReferenceToLocation(BindValueKind kind)
        {
            return (kind & BindValueKind.RefersToLocation) != 0;
        }

        private static bool RequiresAssignableVariable(BindValueKind kind)
        {
            return (kind & BindValueKind.Assignable) != 0;
        }

        private static bool RequiresRefOrOut(BindValueKind kind)
        {
            return (kind & BindValueKind.RefOrOut) == BindValueKind.RefOrOut;
        }

        /// <summary>
        /// Check the expression is of the required lvalue and rvalue specified by valueKind.
        /// The method returns the original expression if the expression is of the required
        /// type. Otherwise, an appropriate error is added to the diagnostics bag and the
        /// method returns a BoundBadExpression node. The method returns the original
        /// expression without generating any error if the expression has errors.
        /// </summary>
        private BoundExpression CheckValue(BoundExpression expr, BindValueKind valueKind, DiagnosticBag diagnostics)
        {
            switch (expr.Kind)
            {
                case BoundKind.PropertyGroup:
                    expr = BindIndexedPropertyAccess((BoundPropertyGroup)expr, mustHaveAllOptionalParameters: false, diagnostics: diagnostics);
                    break;

                case BoundKind.Local:
                    Debug.Assert(expr.Syntax.Kind() != SyntaxKind.Argument || valueKind == BindValueKind.RefOrOut);
                    break;

                case BoundKind.OutVariablePendingInference:
                case BoundKind.OutDeconstructVarPendingInference:
                    Debug.Assert(valueKind == BindValueKind.RefOrOut);
                    return expr;

                case BoundKind.DiscardExpression:
                    Debug.Assert(valueKind == BindValueKind.Assignable || valueKind == BindValueKind.RefOrOut || diagnostics.HasAnyResolvedErrors());
                    return expr;

                case BoundKind.IndexerAccess:
                    {
                        // Assigning to an non ref return indexer needs to set 'useSetterForDefaultArgumentGeneration' to true. 
                        // This is for IOperation purpose.
                        var indexerAccess = (BoundIndexerAccess)expr;
                        if (valueKind == BindValueKind.Assignable && !indexerAccess.Indexer.ReturnsByRef)
                        {
                            expr = indexerAccess.Update(indexerAccess.ReceiverOpt,
                               indexerAccess.Indexer,
                               indexerAccess.Arguments,
                               indexerAccess.ArgumentNamesOpt,
                               indexerAccess.ArgumentRefKindsOpt,
                               indexerAccess.Expanded,
                               indexerAccess.ArgsToParamsOpt,
                               indexerAccess.BinderOpt,
                               useSetterForDefaultArgumentGeneration: true,
                               type: indexerAccess.Type);
                        }
                    }
                    break;
            }

            bool hasResolutionErrors = false;

            // If this a MethodGroup where an rvalue is not expected or where the caller will not explicitly handle
            // (and resolve) MethodGroups (in short, cases where valueKind != BindValueKind.RValueOrMethodGroup),
            // resolve the MethodGroup here to generate the appropriate errors, otherwise resolution errors (such as
            // "member is inaccessible") will be dropped.
            if (expr.Kind == BoundKind.MethodGroup && valueKind != BindValueKind.RValueOrMethodGroup)
            {
                var methodGroup = (BoundMethodGroup)expr;
                HashSet<DiagnosticInfo> useSiteDiagnostics = null;
                var resolution = this.ResolveMethodGroup(methodGroup, analyzedArguments: null, isMethodGroupConversion: false, useSiteDiagnostics: ref useSiteDiagnostics);
                diagnostics.Add(expr.Syntax, useSiteDiagnostics);
                Symbol otherSymbol = null;
                bool resolvedToMethodGroup = resolution.MethodGroup != null;
                if (!expr.HasAnyErrors) diagnostics.AddRange(resolution.Diagnostics); // Suppress cascading.
                hasResolutionErrors = resolution.HasAnyErrors;
                if (hasResolutionErrors)
                {
                    otherSymbol = resolution.OtherSymbol;
                }
                resolution.Free();

                // It's possible the method group is not a method group at all, but simply a
                // delayed lookup that resolved to a non-method member (perhaps an inaccessible
                // field or property), or nothing at all. In those cases, the member should not be exposed as a
                // method group, not even within a BoundBadExpression. Instead, the
                // BoundBadExpression simply refers to the receiver and the resolved symbol (if any).
                if (!resolvedToMethodGroup)
                {
                    Debug.Assert(methodGroup.ResultKind != LookupResultKind.Viable);
                    var receiver = methodGroup.ReceiverOpt;
                    if ((object)otherSymbol != null && receiver?.Kind == BoundKind.TypeOrValueExpression)
                    {
                        // Since we're not accessing a method, this can't be a Color Color case, so TypeOrValueExpression should not have been used.
                        // CAVEAT: otherSymbol could be invalid in some way (e.g. inaccessible), in which case we would have fallen back on a
                        // method group lookup (to allow for extension methods), which would have required a TypeOrValueExpression.
                        Debug.Assert(methodGroup.LookupError != null);

                        // Since we have a concrete member in hand, we can resolve the receiver.
                        var typeOrValue = (BoundTypeOrValueExpression)receiver;
                        receiver = otherSymbol.IsStatic
                            ? null // no receiver required
                            : typeOrValue.Data.ValueExpression;
                    }
                    return new BoundBadExpression(
                        expr.Syntax,
                        methodGroup.ResultKind,
                        (object)otherSymbol == null ? ImmutableArray<Symbol>.Empty : ImmutableArray.Create(otherSymbol),
                        receiver == null ? ImmutableArray<BoundExpression>.Empty : ImmutableArray.Create(receiver),
                        GetNonMethodMemberType(otherSymbol));
                }
            }

            if (!hasResolutionErrors && CheckValueKind(expr.Syntax, expr, valueKind, checkingReceiver: false, diagnostics: diagnostics) ||
                expr.HasAnyErrors && valueKind == BindValueKind.RValueOrMethodGroup)
            {
                return expr;
            }

            var resultKind = (valueKind == BindValueKind.RValue || valueKind == BindValueKind.RValueOrMethodGroup) ?
                LookupResultKind.NotAValue :
                LookupResultKind.NotAVariable;

            return ToBadExpression(expr, resultKind);
        }

        /// <summary>
        /// The purpose of this method is to determine if the expression satisfies desired capabilities. 
        /// If it is not then this code gives an appropriate error message.
        ///
        /// To determine the appropriate error message we need to know two things:
        ///
        /// (1) What capabilities we need - increment it, assign, return as a readonly reference, . . . ?
        ///
        /// (2) Are we trying to determine if the left hand side of a dot is a variable in order
        ///     to determine if the field or property on the right hand side of a dot is assignable?
        ///     
        /// (3) The syntax of the expression that started the analysis. (for error reporting purposes).
        /// </summary>
        internal bool CheckValueKind(SyntaxNode node, BoundExpression expr, BindValueKind valueKind, bool checkingReceiver, DiagnosticBag diagnostics)
        {
            Debug.Assert(!checkingReceiver || expr.Type.IsValueType || expr.Type.IsTypeParameter());

            if (expr.HasAnyErrors)
            {
                return false;
            }

            switch (expr.Kind)
            {
                // we need to handle properties and event in a special way even in an RValue case because of getters
                case BoundKind.PropertyAccess:
                case BoundKind.IndexerAccess:
                    return CheckPropertyValueKind(node, expr, valueKind, checkingReceiver, diagnostics);

                case BoundKind.EventAccess:
                    return CheckEventValueKind((BoundEventAccess)expr, valueKind, diagnostics);
            }

            // easy out for a very common RValue case.
            if (RequiresRValueOnly(valueKind))
            {
                return CheckNotNamespaceOrType(expr, diagnostics);
            }

            // constants/literals are strictly RValues
            // void is not even an RValue
            if ((expr.ConstantValue != null) || (expr.Type.GetSpecialTypeSafe() == SpecialType.System_Void))
            {
                Error(diagnostics, GetStandardLvalueError(valueKind), node);
                return false;
            }

            switch (expr.Kind)
            {
                case BoundKind.NamespaceExpression:
                    var ns = (BoundNamespaceExpression)expr;
                    Error(diagnostics, ErrorCode.ERR_BadSKknown, node, ns.NamespaceSymbol, MessageID.IDS_SK_NAMESPACE.Localize(), MessageID.IDS_SK_VARIABLE.Localize());
                    return false;

                case BoundKind.TypeExpression:
                    var type = (BoundTypeExpression)expr;
                    Error(diagnostics, ErrorCode.ERR_BadSKknown, node, type.Type, MessageID.IDS_SK_TYPE.Localize(), MessageID.IDS_SK_VARIABLE.Localize());
                    return false;

                case BoundKind.Lambda:
                case BoundKind.UnboundLambda:
                    // lambdas can only be used as RValues
                    Error(diagnostics, GetStandardLvalueError(valueKind), node);
                    return false;

                case BoundKind.MethodGroup:
                    // method groups can only be used as RValues
                    var methodGroup = (BoundMethodGroup)expr;
                    Error(diagnostics, GetMethodGroupLvalueError(valueKind), node, methodGroup.Name, MessageID.IDS_MethodGroup.Localize());
                    return false;

                case BoundKind.RangeVariable:
                    // range variables can only be used as RValues
                    var queryref = (BoundRangeVariable)expr;
                    Error(diagnostics, GetRangeLvalueError(valueKind), node, queryref.RangeVariableSymbol.Name);
                    return false;

                case BoundKind.Conversion:
                    var conversion = (BoundConversion)expr;
                    // conversions are strict RValues, but unboxing has a specific error
                    if (conversion.ConversionKind == ConversionKind.Unboxing)
                    {
                        Error(diagnostics, ErrorCode.ERR_UnboxNotLValue, node);
                        return false;
                    }
                    break;

                case BoundKind.ArrayAccess:
                case BoundKind.PointerIndirectionOperator:
                case BoundKind.PointerElementAccess:
                    // array elements and pointer dereferencing are readwrite variables
                    return true;

                case BoundKind.RefValueOperator:
                    // The undocumented __refvalue(tr, T) expression results in a variable of type T.
                    // it is a readwrite variable.
                    return true;

                case BoundKind.DynamicMemberAccess:
                case BoundKind.DynamicIndexerAccess:
                    // dynamic expressions can be read and written to
                    // can even be passed by reference (which is implemented via a temp)
                    return true;

                case BoundKind.Parameter:
                    var parameter = (BoundParameter)expr;
                    return CheckParameterValueKind(node, parameter, valueKind, checkingReceiver, diagnostics);

                case BoundKind.Local:
                    var local = (BoundLocal)expr;
                    return CheckLocalValueKind(node, local, valueKind, checkingReceiver, diagnostics);

                case BoundKind.ThisReference:
                    var thisref = (BoundThisReference)expr;

                    // We will already have given an error for "this" used outside of a constructor, 
                    // instance method, or instance accessor. Assume that "this" is a variable if it is in a struct.

                    // SPEC: when this is used in a primary-expression within an instance constructor of a struct, 
                    // SPEC: it is classified as a variable. 

                    // SPEC: When this is used in a primary-expression within an instance method or instance accessor
                    // SPEC: of a struct, it is classified as a variable. 

                    // Note: RValueOnly is checked at the beginning of this method. Since we are here we need more than readable.
                    //"this" is readonly in members of readonly structs, unless we are in a constructor.
                    if (!thisref.Type.IsValueType ||
                        (thisref.Type.IsReadOnly && (this.ContainingMemberOrLambda as MethodSymbol)?.MethodKind != MethodKind.Constructor))
                    {
                        // CONSIDER: the Dev10 name has angle brackets (i.e. "<this>")
                        Error(diagnostics, GetThisLvalueError(valueKind), node, ThisParameterSymbol.SymbolName);
                        return false;
                    }

                    return true;

                case BoundKind.Call:
                    var call = (BoundCall)expr;
                    return CheckCallValueKind(call, node, valueKind, checkingReceiver, diagnostics);

                case BoundKind.ConditionalOperator:
                    var conditional = (BoundConditionalOperator)expr;

                    // byref conditional defers to its operands
                    if (conditional.IsByRef &&
                        (CheckValueKind(conditional.Consequence.Syntax, conditional.Consequence, valueKind, checkingReceiver: false, diagnostics: diagnostics) &
                        CheckValueKind(conditional.Alternative.Syntax, conditional.Alternative, valueKind, checkingReceiver: false, diagnostics: diagnostics)))
                    {
                        return true;
                    }

                    // report standard lvalue error
                    break;

                case BoundKind.FieldAccess:
                    var fieldAccess = (BoundFieldAccess)expr;
                    return CheckFieldValueKind(node, fieldAccess, valueKind, checkingReceiver, diagnostics);
            }

            // At this point we should have covered all the possible cases for anything that is not a strict RValue.
            Error(diagnostics, GetStandardLvalueError(valueKind), node);
            return false;
        }

        private static bool CheckNotNamespaceOrType(BoundExpression expr, DiagnosticBag diagnostics)
        {
            switch (expr.Kind)
            {
                case BoundKind.NamespaceExpression:
                    Error(diagnostics, ErrorCode.ERR_BadSKknown, expr.Syntax, ((BoundNamespaceExpression)expr).NamespaceSymbol, MessageID.IDS_SK_NAMESPACE.Localize(), MessageID.IDS_SK_VARIABLE.Localize());
                    return false;
                case BoundKind.TypeExpression:
                    Error(diagnostics, ErrorCode.ERR_BadSKunknown, expr.Syntax, expr.Type, MessageID.IDS_SK_TYPE.Localize());
                    return false;
                default:
                    return true;
            }
        }

        private bool CheckLocalValueKind(SyntaxNode node, BoundLocal local, BindValueKind valueKind, bool checkingReceiver, DiagnosticBag diagnostics)
        {
            // Local constants are never variables. Local variables are sometimes
            // not to be treated as variables, if they are fixed, declared in a using, 
            // or declared in a foreach.

            LocalSymbol localSymbol = local.LocalSymbol;
            if (RequiresAssignableVariable(valueKind))
            {
                if (this.LockedOrDisposedVariables.Contains(localSymbol))
                {
                    diagnostics.Add(ErrorCode.WRN_AssignmentToLockOrDispose, local.Syntax.Location, localSymbol);
                }

                if (!localSymbol.IsWritable)
                {
                    ReportReadonlyLocalError(node, localSymbol, valueKind, checkingReceiver, diagnostics);
                    return false;
                }
            }

            return true;
        }

        private static bool CheckLocalRefEscape(SyntaxNode node, BoundLocal local, uint escapeTo, bool checkingReceiver, DiagnosticBag diagnostics)
        {
            LocalSymbol localSymbol = local.LocalSymbol;

            // if local symbol can escape to the same or wider/shallower scope then escapeTo
            // then it is all ok, otherwise it is an error.
            if (localSymbol.RefEscapeScope <= escapeTo)
            {
                return true;
            }

            if (escapeTo == Binder.ExternalScope)
            {
                if (localSymbol.RefKind == RefKind.None)
                {
                    if (checkingReceiver)
                    {
                        Error(diagnostics, ErrorCode.ERR_RefReturnLocal2, local.Syntax, localSymbol);
                    }
                    else
                    {
                        Error(diagnostics, ErrorCode.ERR_RefReturnLocal, node, localSymbol);
                    }
                    return false;
                }

                if (checkingReceiver)
                {
                    Error(diagnostics, ErrorCode.ERR_RefReturnNonreturnableLocal2, local.Syntax, localSymbol);
                }
                else
                {
                    Error(diagnostics, ErrorCode.ERR_RefReturnNonreturnableLocal, node, localSymbol);
                }
                return false;
            }

            Error(diagnostics, ErrorCode.ERR_EscapeLocal, node, localSymbol);
            return false;
        }

        private bool CheckParameterValueKind(SyntaxNode node, BoundParameter parameter, BindValueKind valueKind, bool checkingReceiver, DiagnosticBag diagnostics)
        {
            ParameterSymbol parameterSymbol = parameter.ParameterSymbol;

            // all parameters can be passed by ref/out or assigned to
            // except "in" parameters, which are readonly
            if (parameterSymbol.RefKind == RefKind.In && RequiresAssignableVariable(valueKind))
            {
                ReportReadOnlyError(parameterSymbol, node, valueKind, checkingReceiver, diagnostics);
                return false;
            }

            if (this.LockedOrDisposedVariables.Contains(parameterSymbol))
            {
                // Consider: It would be more conventional to pass "symbol" rather than "symbol.Name".
                // The issue is that the error SymbolDisplayFormat doesn't display parameter
                // names - only their types - which works great in signatures, but not at all
                // at the top level.
                diagnostics.Add(ErrorCode.WRN_AssignmentToLockOrDispose, parameter.Syntax.Location, parameterSymbol.Name);
            }

            return true;
        }

        private static bool CheckParameterRefEscape(SyntaxNode node, BoundParameter parameter, uint escapeTo, bool checkingReceiver, DiagnosticBag diagnostics)
        {
            ParameterSymbol parameterSymbol = parameter.ParameterSymbol;

            // byval parameters can escape to method's top level.
            // others can be escape further, unless they are ref-like.
            if (escapeTo == Binder.ExternalScope && parameterSymbol.RefKind == RefKind.None)
            {
                if (checkingReceiver)
                {
                    Error(diagnostics, ErrorCode.ERR_RefReturnParameter2, parameter.Syntax, parameterSymbol.Name);
                }
                else
                {
                    Error(diagnostics, ErrorCode.ERR_RefReturnParameter, node, parameterSymbol.Name);
                }
                return false;
            }

            // can ref-escape to any scope otherwise
            return true;
        }

        private bool CheckFieldValueKind(SyntaxNode node, BoundFieldAccess fieldAccess, BindValueKind valueKind, bool checkingReceiver, DiagnosticBag diagnostics)
        {
            var fieldSymbol = fieldAccess.FieldSymbol;
            var fieldIsStatic = fieldSymbol.IsStatic;

            if (RequiresAssignableVariable(valueKind))
            {
                // A field is writeable unless 
                // (1) it is readonly and we are not in a constructor or field initializer
                // (2) the receiver of the field is of value type and is not a variable or object creation expression.
                // For example, if you have a class C with readonly field f of type S, and
                // S has a mutable field x, then c.f.x is not a variable because c.f is not
                // writable.

                if (fieldSymbol.IsReadOnly)
                {
                    var canModifyReadonly = false;

                    Symbol containing = this.ContainingMemberOrLambda;
                    if ((object)containing != null &&
                        fieldIsStatic == containing.IsStatic &&
                        (fieldIsStatic || fieldAccess.ReceiverOpt.Kind == BoundKind.ThisReference) &&
                        (Compilation.FeatureStrictEnabled
                            ? fieldSymbol.ContainingType == containing.ContainingType
                            // We duplicate a bug in the native compiler for compatibility in non-strict mode
                            : fieldSymbol.ContainingType.OriginalDefinition == containing.ContainingType.OriginalDefinition))
                    {
                        if (containing.Kind == SymbolKind.Method)
                        {
                            MethodSymbol containingMethod = (MethodSymbol)containing;
                            MethodKind desiredMethodKind = fieldIsStatic ? MethodKind.StaticConstructor : MethodKind.Constructor;
                            canModifyReadonly = containingMethod.MethodKind == desiredMethodKind;
                        }
                        else if (containing.Kind == SymbolKind.Field)
                        {
                            canModifyReadonly = true;
                        }
                    }

                    if (!canModifyReadonly)
                    {
                        ReportReadOnlyFieldError(fieldSymbol, node, valueKind, checkingReceiver, diagnostics);
                        return false;
                    }
                }

                if (fieldSymbol.IsFixed)
                {
                    Error(diagnostics, GetStandardLvalueError(valueKind), node);
                    return false;
                }
            }

            // r/w fields that are static or belong to reference types are writeable and returnable
            if (fieldIsStatic || fieldSymbol.ContainingType.IsReferenceType)
            {
                return true;
            }

            // for other fields defer to the receiver.
            return CheckIsValidReceiverForVariable(node, fieldAccess.ReceiverOpt, valueKind, diagnostics);
        }

        private static bool CheckFieldRefEscape(SyntaxNode node, BoundFieldAccess fieldAccess, uint escapeFrom, uint escapeTo, bool checkingReceiver, DiagnosticBag diagnostics)
        {
            var fieldSymbol = fieldAccess.FieldSymbol;
            // fields that are static or belong to reference types can ref escape anywhere
            if (fieldSymbol.IsStatic || fieldSymbol.ContainingType.IsReferenceType)
            {
                return true;
            }

            // for other fields defer to the receiver.
            return CheckRefEscape(node, fieldAccess.ReceiverOpt, escapeFrom, escapeTo, checkingReceiver: true, diagnostics: diagnostics);
        }

        private static bool CheckFieldLikeEventRefEscape(SyntaxNode node, BoundEventAccess eventAccess, uint escapeFrom, uint escapeTo, bool checkingReceiver, DiagnosticBag diagnostics)
        {
            var eventSymbol = eventAccess.EventSymbol;

            // field-like events that are static or belong to reference types can ref escape anywhere
            if (eventSymbol.IsStatic || eventSymbol.ContainingType.IsReferenceType)
            {
                return true;
            }

            // for other events defer to the receiver.
            return CheckRefEscape(node, eventAccess.ReceiverOpt, escapeFrom, escapeTo, checkingReceiver: true, diagnostics: diagnostics);
        }

        private bool CheckEventValueKind(BoundEventAccess boundEvent, BindValueKind valueKind, DiagnosticBag diagnostics)
        {
            // Compound assignment (actually "event assignment") is allowed "everywhere", subject to the restrictions of
            // accessibility, use site errors, and receiver variable-ness (for structs).
            // Other operations are allowed only for field-like events and only where the backing field is accessible
            // (i.e. in the declaring type) - subject to use site errors and receiver variable-ness.

            BoundExpression receiver = boundEvent.ReceiverOpt;
            SyntaxNode eventSyntax = GetEventName(boundEvent); //does not include receiver
            EventSymbol eventSymbol = boundEvent.EventSymbol;

            if (valueKind == BindValueKind.CompoundAssignment)
            {
                // NOTE: accessibility has already been checked by lookup.
                // NOTE: availability of well-known members is checked in BindEventAssignment because
                // we don't have the context to determine whether addition or subtraction is being performed.

                if (receiver?.Kind == BoundKind.BaseReference && eventSymbol.IsAbstract)
                {
                    Error(diagnostics, ErrorCode.ERR_AbstractBaseCall, boundEvent.Syntax, eventSymbol);
                    return false;
                }
                else if (ReportUseSiteDiagnostics(eventSymbol, diagnostics, eventSyntax))
                {
                    // NOTE: BindEventAssignment checks use site errors on the specific accessor 
                    // (since we don't know which is being used).
                    return false;
                }

                Debug.Assert(!RequiresVariableReceiver(receiver, eventSymbol));
                return true;
            }
            else
            {
                if (!boundEvent.IsUsableAsField)
                {
                    // Dev10 reports this in addition to ERR_BadAccess, but we won't even reach this point if the event isn't accessible (caught by lookup).
                    Error(diagnostics, GetBadEventUsageDiagnosticInfo(eventSymbol), eventSyntax);
                    return false;
                }
                else if (ReportUseSiteDiagnostics(eventSymbol, diagnostics, eventSyntax))
                {
                    if (!CheckIsValidReceiverForVariable(eventSyntax, receiver, BindValueKind.Assignable, diagnostics))
                    {
                        return false;
                    }
                }
                else if (RequiresVariable(valueKind))
                {
                    if (eventSymbol.IsWindowsRuntimeEvent && valueKind != BindValueKind.Assignable)
                    {
                        // NOTE: Dev11 reports ERR_RefProperty, as if this were a property access (since that's how it will be lowered).
                        // Roslyn reports a new, more specific, error code.
                        ErrorCode errorCode = valueKind == BindValueKind.RefOrOut ? ErrorCode.ERR_WinRtEventPassedByRef : GetStandardLvalueError(valueKind);
                        Error(diagnostics, errorCode, eventSyntax, eventSymbol);

                        return false;
                    }
                    else if (RequiresVariableReceiver(receiver, eventSymbol.AssociatedField) && // NOTE: using field, not event
                        !CheckIsValidReceiverForVariable(eventSyntax, receiver, valueKind, diagnostics))
                    {
                        return false;
                    }
                }

                return true;
            }
        }

        private bool CheckIsValidReceiverForVariable(SyntaxNode node, BoundExpression receiver, BindValueKind kind, DiagnosticBag diagnostics)
        {
            Debug.Assert(receiver != null);
            return Flags.Includes(BinderFlags.ObjectInitializerMember) && receiver.Kind == BoundKind.ImplicitReceiver ||
                CheckValueKind(node, receiver, kind, true, diagnostics);
        }

        /// <summary>
        /// SPEC: When a property or indexer declared in a struct-type is the target of an 
        /// SPEC: assignment, the instance expression associated with the property or indexer 
        /// SPEC: access must be classified as a variable. If the instance expression is 
        /// SPEC: classified as a value, a compile-time error occurs. Because of 7.6.4, 
        /// SPEC: the same rule also applies to fields.
        /// </summary>
        /// <remarks>
        /// NOTE: The spec fails to impose the restriction that the event receiver must be classified
        /// as a variable (unlike for properties - 7.17.1).  This seems like a bug, but we have
        /// production code that won't build with the restriction in place (see DevDiv #15674).
        /// </remarks>
        private static bool RequiresVariableReceiver(BoundExpression receiver, Symbol symbol)
        {
            return !symbol.IsStatic
                && symbol.Kind != SymbolKind.Event
                && receiver?.Type?.IsValueType == true;
        }

        private bool CheckCallValueKind(BoundCall call, SyntaxNode node, BindValueKind valueKind, bool checkingReceiver, DiagnosticBag diagnostics)
        {
            // A call can only be a variable if it returns by reference. If this is the case,
            // whether or not it is a valid variable depends on whether or not the call is the
            // RHS of a return or an assign by reference:
            // - If call is used in a context demanding ref-returnable reference all of its ref
            //   inputs must be ref-returnable
            var methodSymbol = call.Method;
            var callSyntax = call.Syntax;

            if (RequiresVariable(valueKind) && methodSymbol.RefKind == RefKind.None)
            {
                if (checkingReceiver)
                {
                    // Error is associated with expression, not node which may be distinct.
                    Error(diagnostics, ErrorCode.ERR_ReturnNotLValue, callSyntax, methodSymbol);
                }
                else
                {
                    Error(diagnostics, GetStandardLvalueError(valueKind), node);
                }

                return false;
            }

            if (RequiresAssignableVariable(valueKind) && methodSymbol.RefKind == RefKind.RefReadOnly)
            {
                ReportReadOnlyError(methodSymbol, node, valueKind, checkingReceiver, diagnostics);
                return false;
            }

            return true;
        }

        private bool CheckPropertyValueKind(SyntaxNode node, BoundExpression expr, BindValueKind valueKind, bool checkingReceiver, DiagnosticBag diagnostics)
        {
            // SPEC: If the left operand is a property or indexer access, the property or indexer must
            // SPEC: have a set accessor. If this is not the case, a compile-time error occurs.

            // Addendum: Assignment is also allowed for get-only autoprops in their constructor

            BoundExpression receiver;
            SyntaxNode propertySyntax;
            var propertySymbol = GetPropertySymbol(expr, out receiver, out propertySyntax);

            Debug.Assert((object)propertySymbol != null);
            Debug.Assert(propertySyntax != null);

            if ((RequiresReferenceToLocation(valueKind) || checkingReceiver) &&
                propertySymbol.RefKind == RefKind.None)
            {
                if (checkingReceiver)
                {
                    // Error is associated with expression, not node which may be distinct.
                    // This error is reported for all values types. That is a breaking
                    // change from Dev10 which reports this error for struct types only,
                    // not for type parameters constrained to "struct".

                    Debug.Assert((object)propertySymbol.Type != null);
                    Error(diagnostics, ErrorCode.ERR_ReturnNotLValue, expr.Syntax, propertySymbol);
                }
                else
                {
                    Error(diagnostics, valueKind == BindValueKind.RefOrOut ? ErrorCode.ERR_RefProperty : GetStandardLvalueError(valueKind), node, propertySymbol);
                }

                return false;
            }

            if (RequiresAssignableVariable(valueKind) && propertySymbol.RefKind == RefKind.RefReadOnly)
            {
                ReportReadOnlyError(propertySymbol, node, valueKind, checkingReceiver, diagnostics);
                return false;
            }

            var requiresSet = RequiresAssignableVariable(valueKind) && propertySymbol.RefKind == RefKind.None;
            if (requiresSet)
            {
                var setMethod = propertySymbol.GetOwnOrInheritedSetMethod();

                if ((object)setMethod == null)
                {
                    var containing = this.ContainingMemberOrLambda;
                    if (!AccessingAutoPropertyFromConstructor(receiver, propertySymbol, containing))
                    {
                        Error(diagnostics, ErrorCode.ERR_AssgReadonlyProp, node, propertySymbol);
                        return false;
                    }
                }
                else if (receiver?.Kind == BoundKind.BaseReference && setMethod.IsAbstract)
                {
                    Error(diagnostics, ErrorCode.ERR_AbstractBaseCall, node, propertySymbol);
                    return false;
                }
                else if (!object.Equals(setMethod.GetUseSiteDiagnostic(), propertySymbol.GetUseSiteDiagnostic()) && ReportUseSiteDiagnostics(setMethod, diagnostics, propertySyntax))
                {
                    return false;
                }
                else
                {
                    var accessThroughType = this.GetAccessThroughType(receiver);
                    bool failedThroughTypeCheck;
                    HashSet<DiagnosticInfo> useSiteDiagnostics = null;
                    bool isAccessible = this.IsAccessible(setMethod, accessThroughType, out failedThroughTypeCheck, ref useSiteDiagnostics);
                    diagnostics.Add(node, useSiteDiagnostics);

                    if (!isAccessible)
                    {
                        if (failedThroughTypeCheck)
                        {
                            Error(diagnostics, ErrorCode.ERR_BadProtectedAccess, node, propertySymbol, accessThroughType, this.ContainingType);
                        }
                        else
                        {
                            Error(diagnostics, ErrorCode.ERR_InaccessibleSetter, node, propertySymbol);
                        }
                        return false;
                    }

                    ReportDiagnosticsIfObsolete(diagnostics, setMethod, node, receiver?.Kind == BoundKind.BaseReference);

                    if (RequiresVariableReceiver(receiver, setMethod) && !CheckIsValidReceiverForVariable(node, receiver, BindValueKind.Assignable, diagnostics))
                    {
                        return false;
                    }
                }
            }

            var requiresGet = !RequiresAssignmentOnly(valueKind) || propertySymbol.RefKind != RefKind.None;
            if (requiresGet)
            {
                var getMethod = propertySymbol.GetOwnOrInheritedGetMethod();

                if ((object)getMethod == null)
                {
                    Error(diagnostics, ErrorCode.ERR_PropertyLacksGet, node, propertySymbol);
                    return false;
                }
                else if (receiver?.Kind == BoundKind.BaseReference && getMethod.IsAbstract)
                {
                    Error(diagnostics, ErrorCode.ERR_AbstractBaseCall, node, propertySymbol);
                    return false;
                }
                else if (!object.Equals(getMethod.GetUseSiteDiagnostic(), propertySymbol.GetUseSiteDiagnostic()) && ReportUseSiteDiagnostics(getMethod, diagnostics, propertySyntax))
                {
                    return false;
                }
                else
                {
                    var accessThroughType = this.GetAccessThroughType(receiver);
                    bool failedThroughTypeCheck;
                    HashSet<DiagnosticInfo> useSiteDiagnostics = null;
                    bool isAccessible = this.IsAccessible(getMethod, accessThroughType, out failedThroughTypeCheck, ref useSiteDiagnostics);
                    diagnostics.Add(node, useSiteDiagnostics);

                    if (!isAccessible)
                    {
                        if (failedThroughTypeCheck)
                        {
                            Error(diagnostics, ErrorCode.ERR_BadProtectedAccess, node, propertySymbol, accessThroughType, this.ContainingType);
                        }
                        else
                        {
                            Error(diagnostics, ErrorCode.ERR_InaccessibleGetter, node, propertySymbol);
                        }
                        return false;
                    }

                    ReportDiagnosticsIfObsolete(diagnostics, getMethod, node, receiver?.Kind == BoundKind.BaseReference);
                }
            }

            return true;
        }

        /// <summary>
        /// Computes the scope to which the given invocation can escape
        /// NOTE: the escape scope for ref and val escapes is the same for invocations except for trivial cases (ordinary type returned by val) 
        ///       where escape is known otherwise. Therefore we do not vave two ref/val variants of this.
        ///       
        /// NOTE: we need scopeOfTheContainingExpression as some expressions such as optional `in` parameters or `ref dynamic` behave as 
        ///       local variables declared at the scope of the invocation.
        /// </summary>
        private static uint GetInvocationEscapeScope(
            Symbol symbol,
            BoundExpression receiverOpt,
            ImmutableArray<ParameterSymbol> parameters,
            ImmutableArray<BoundExpression> argsOpt,
            ImmutableArray<RefKind> argRefKindsOpt,
            ImmutableArray<int> argsToParamsOpt,
            uint scopeOfTheContainingExpression,
            bool isRefEscape
        )
        {
            // SPEC: (also applies to the CheckInvocationEscape counterpart)
            //
            //            An lvalue resulting from a ref-returning method invocation e1.M(e2, ...) is ref-safe - to - escape the smallest of the following scopes:
            //•	The entire enclosing method
            //•	the ref-safe-to-escape of all ref/out/in argument expressions(excluding the receiver)
            //•	the safe-to - escape of all argument expressions(including the receiver)
            //
            //            An rvalue resulting from a method invocation e1.M(e2, ...) is safe - to - escape from the smallest of the following scopes:
            //•	The entire enclosing method
            //•	the safe-to-escape of all argument expressions(including the receiver)
            //

            if (symbol.IsStatic)
            {
                // ignore receiver when symbol is static
                receiverOpt = null;
            }

            //by default it is safe to escape
            uint escapeScope = Binder.ExternalScope;

            ArrayBuilder<bool> refReadOnlyParametersMatchedWithArgs = null;

            if (!argsOpt.IsDefault)
            {
moreArguments:
                for (var argIndex = 0; argIndex < argsOpt.Length; argIndex++)
                {
                    var argument = argsOpt[argIndex];
                    if (argument.Kind == BoundKind.ArgListOperator)
                    {
                        Debug.Assert(argIndex == argsOpt.Length - 1, "vararg must be the last");
                        var argList = (BoundArgListOperator)argument;

                        // unwrap varargs and process as more arguments
                        argsOpt = argList.Arguments;
                        // ref kinds of varargs are not interesting here. 
                        // __refvalue is not ref-returnable, so ref varargs can't come back from a call
                        argRefKindsOpt = default;
                        parameters = ImmutableArray<ParameterSymbol>.Empty;
                        argsToParamsOpt = default;

                        goto moreArguments;
                    }

                    RefKind effectiveRefKind = GetEffectiveRefKind(argIndex, argRefKindsOpt, parameters, argsToParamsOpt, ref refReadOnlyParametersMatchedWithArgs);

                    // ref escape scope is the narrowest of 
                    // - ref escape of all byref arguments
                    // - val escape of all byval arguments  (ref-like values can be unwrapped into refs, so treat val escape of values as possible ref escape of the result)
                    //
                    // val escape scope is the narrowest of 
                    // - val escape of all byval arguments  (refs cannot be wrapped into values, so their ref escape is irrelevant, only use val escapes)

                    var argEscape = effectiveRefKind != RefKind.None && isRefEscape ?
                                        GetRefEscape(argument, scopeOfTheContainingExpression) :
                                        GetValEscape(argument, scopeOfTheContainingExpression);

                    escapeScope = Math.Max(escapeScope, argEscape);

                    if (escapeScope >= scopeOfTheContainingExpression)
                    {
                        // no longer needed
                        refReadOnlyParametersMatchedWithArgs?.Free();

                        // can't get any worse
                        return escapeScope;
                    }
                }
            }

            // handle omitted optional "in" parameters if there are any
            ParameterSymbol unmatchedRefReadOnlyParameter = TryGetUnmatchedRefReadOnlyParameterAndFreeMatchedArgs(parameters, ref refReadOnlyParametersMatchedWithArgs);

            // unmatched "in" parameter is the same as a literal, its ref escape is scopeOfTheContainingExpression  (can't get any worse)
            //                                                    its val escape is ExternalScope                   (does not affect overal result)
            if (unmatchedRefReadOnlyParameter != null && isRefEscape)
            {
                return scopeOfTheContainingExpression;
            }

            // check receiver if ref-like
            if (receiverOpt?.Type?.IsByRefLikeType == true)
            {
                return GetValEscape(receiverOpt, scopeOfTheContainingExpression);
            }

            return escapeScope;
        }

        /// <summary>
        /// Validates whether given invocation can allow its results to escape from `escapeFrom` level to `escapeTo` level.
        /// The result indicates whether the escape is possible. 
        /// Additionally, the method emits diagnostics (possibly more than one, recursively) that would help identify the cause for the failure.
        /// 
        /// NOTE: we need scopeOfTheContainingExpression as some expressions such as optional `in` parameters or `ref dynamic` behave as 
        ///       local variables declared at the scope of the invocation.
        /// </summary>
        private static bool CheckInvocationEscape(
            SyntaxNode syntax,
            Symbol symbol,
            BoundExpression receiverOpt,
            ImmutableArray<ParameterSymbol> parameters,
            ImmutableArray<BoundExpression> argsOpt,
            ImmutableArray<RefKind> argRefKindsOpt,
            ImmutableArray<int> argsToParamsOpt,
            bool checkingReceiver,
            uint escapeFrom,
            uint escapeTo,
            DiagnosticBag diagnostics,
            bool isRefEscape
        )
        {
            // SPEC: 
            //            In a method invocation, the following constraints apply:
            //•	If there is a ref or out argument to a ref struct type (including the receiver), with safe-to-escape E1, then
            //  o no ref or out argument(excluding the receiver and arguments of ref-like types) may have a narrower ref-safe-to-escape than E1; and
            //  o   no argument(including the receiver) may have a narrower safe-to-escape than E1.

            if (symbol.IsStatic)
            {
                // ignore receiver when symbol is static
                receiverOpt = null;
            }

            ArrayBuilder<bool> refReadOnlyParametersMatchedWithArgs = null;

            if (!argsOpt.IsDefault)
            {

moreArguments:
                for (var argIndex = 0; argIndex < argsOpt.Length; argIndex++)
                {
                    var argument = argsOpt[argIndex];
                    if (argument.Kind == BoundKind.ArgListOperator)
                    {
                        Debug.Assert(argIndex == argsOpt.Length - 1, "vararg must be the last");
                        var argList = (BoundArgListOperator)argument;

                        // unwrap varargs and process as more arguments
                        argsOpt = argList.Arguments;
                        // ref kinds of varargs are not interesting here. 
                        // __refvalue is not ref-returnable, so ref varargs can't come back from a call
                        argRefKindsOpt = default;
                        parameters = ImmutableArray<ParameterSymbol>.Empty;
                        argsToParamsOpt = default;

                        goto moreArguments;
                    }

                    RefKind effectiveRefKind = GetEffectiveRefKind(argIndex, argRefKindsOpt, parameters, argsToParamsOpt, ref refReadOnlyParametersMatchedWithArgs);

                    // ref escape scope is the narrowest of 
                    // - ref escape of all byref arguments
                    // - val escape of all byval arguments  (ref-like values can be unwrapped into refs, so treat val escape of values as possible ref escape of the result)
                    //
                    // val escape scope is the narrowest of 
                    // - val escape of all byval arguments  (refs cannot be wrapped into values, so their ref escape is irrelevant, only use val escapes)
                    var valid = effectiveRefKind != RefKind.None && isRefEscape ?
                                        CheckRefEscape(argument.Syntax, argument, escapeFrom, escapeTo, false, diagnostics) :
                                        CheckValEscape(argument.Syntax, argument, escapeFrom, escapeTo, false, diagnostics);

                    if (!valid)
                    {
                        // no longer needed
                        refReadOnlyParametersMatchedWithArgs?.Free();

                        ErrorCode errorCode = GetStandardCallEscapeError(checkingReceiver);

                        string parameterName;
                        if (parameters.Length > 0)
                        {
                            var paramIndex = argsToParamsOpt.IsDefault ? argIndex : argsToParamsOpt[argIndex];
                            parameterName = parameters[paramIndex].Name;
                        }
                        else
                        {
                            parameterName = "__arglist";
                        }

                        Error(diagnostics, errorCode, syntax, symbol, parameterName);
                        return false;
                    }
                }
            }

            // handle omitted optional "in" parameters if there are any
            ParameterSymbol unmatchedRefReadOnlyParameter = TryGetUnmatchedRefReadOnlyParameterAndFreeMatchedArgs(parameters, ref refReadOnlyParametersMatchedWithArgs);

            // unmatched "in" parameter is the same as a literal, its ref escape is scopeOfTheContainingExpression  (can't get any worse)
            //                                                    its val escape is ExternalScope                   (does not affect overal result)
            if (unmatchedRefReadOnlyParameter != null && isRefEscape)
            {
                Error(diagnostics, GetStandardCallEscapeError(checkingReceiver), syntax, symbol, unmatchedRefReadOnlyParameter.Name);
                return false;
            }

            // check receiver if ref-like
            if (receiverOpt?.Type?.IsByRefLikeType == true)
            {
                return CheckValEscape(receiverOpt.Syntax, receiverOpt, escapeFrom, escapeTo, false, diagnostics);
            }

            return true;
        }

        /// <summary>
        /// Validates whether the invocation is valid per no-mixing rules.
        /// Returns `false` when it is not valid and produces diagnostics (possibly more than one recursively) that helps to figure the reason.
        /// </summary>
        private static bool CheckInvocationArgMixing(
            SyntaxNode syntax,
            Symbol symbol,
            BoundExpression receiverOpt,
            ImmutableArray<ParameterSymbol> parameters,
            ImmutableArray<BoundExpression> argsOpt,
            ImmutableArray<RefKind> argRefKindsOpt,
            ImmutableArray<int> argsToParamsOpt,
            uint scopeOfTheContainingExpression,
            DiagnosticBag diagnostics)
        {
            if (symbol.IsStatic)
            {
                // ignore receiver when symbol is static
                receiverOpt = null;
            }

            // widest possible escape via writeable ref-like receiver or ref/out argument.
            uint escapeTo = scopeOfTheContainingExpression;

            // collect all writeable ref-like arguments, including receiver
            var receiverType = receiverOpt?.Type;
            if (receiverType?.IsByRefLikeType == true && receiverType?.IsReadOnly == false)
            {
                escapeTo = GetValEscape(receiverOpt, scopeOfTheContainingExpression);
            }

            if (!argsOpt.IsDefault)
            {
                BoundArgListOperator argList = null;
                for (var argIndex = 0; argIndex < argsOpt.Length; argIndex++)
                {
                    var argument = argsOpt[argIndex];
                    if (argument.Kind == BoundKind.ArgListOperator)
                    {
                        Debug.Assert(argIndex == argsOpt.Length - 1, "vararg must be the last");
                        argList = (BoundArgListOperator)argument;
                        break;
                    }

                    var refKind = argRefKindsOpt.IsDefault ? RefKind.None : argRefKindsOpt[argIndex];
                    if (refKind != RefKind.None && argument.Type?.IsByRefLikeType == true)
                    {
                        Debug.Assert(refKind == RefKind.Ref || refKind == RefKind.Out);
                        escapeTo = Math.Min(escapeTo, GetValEscape(argument, scopeOfTheContainingExpression));
                    }
                }

                if (argList != null)
                {
                    var argListArgs = argList.Arguments;
                    var argListRefKindsOpt = argList.ArgumentRefKindsOpt;

                    for (var argIndex = 0; argIndex < argListArgs.Length; argIndex++)
                    {
                        var argument = argListArgs[argIndex];
                        var refKind = argListRefKindsOpt.IsDefault ? RefKind.None : argListRefKindsOpt[argIndex];
                        if (refKind != RefKind.None && argument.Type?.IsByRefLikeType == true)
                        {
                            Debug.Assert(refKind == RefKind.Ref || refKind == RefKind.Out);
                            escapeTo = Math.Min(escapeTo, GetValEscape(argument, scopeOfTheContainingExpression));
                        }
                    }
                }
            }

            if (escapeTo == scopeOfTheContainingExpression)
            {
                // cannot fail. common case.
                return true;
            }

            if (!argsOpt.IsDefault)
            {
 moreArguments:
                for (var argIndex = 0; argIndex < argsOpt.Length; argIndex++)
                {
                    // check val escape of all arguments
                    var argument = argsOpt[argIndex];
                    if (argument.Kind == BoundKind.ArgListOperator)
                    {
                        Debug.Assert(argIndex == argsOpt.Length - 1, "vararg must be the last");
                        var argList = (BoundArgListOperator)argument;

                        // unwrap varargs and process as more arguments
                        argsOpt = argList.Arguments;
                        parameters = ImmutableArray<ParameterSymbol>.Empty;
                        argsToParamsOpt = default;

                        goto moreArguments;
                    }

                    var valid = CheckValEscape(argument.Syntax, argument, scopeOfTheContainingExpression, escapeTo, false, diagnostics);

                    if (!valid)
                    {
                        string parameterName;
                        if (parameters.Length > 0)
                        {
                            var paramIndex = argsToParamsOpt.IsDefault ? argIndex : argsToParamsOpt[argIndex];
                            parameterName = parameters[paramIndex].Name;
                        }
                        else
                        {
                            parameterName = "__arglist";
                        }

                        Error(diagnostics, ErrorCode.ERR_CallArgMixing, syntax, symbol, parameterName);
                        return false;
                    }
                }
            }

            //NB: we do not care about unmatched "in" parameters here. 
            //    They have "outer" val escape, so cannot be worse than escapeTo.

            // check val escape of receiver if ref-like
            if (receiverOpt?.Type?.IsByRefLikeType == true)
            {
                return CheckValEscape(receiverOpt.Syntax, receiverOpt, scopeOfTheContainingExpression, escapeTo, false, diagnostics);
            }

            return true;
        }

        /// <summary>
        /// Gets "effective" ref kind of an argument. 
        /// Generally we know if a formal argument is passed as ref/out by looking at the call site. 
        /// However, to distinguish "in" and regular "val" parameters we need to take a look at corresponding parameter, if such exists. 
        /// NOTE: there are cases like params/vararg, when a corresponding parameter may not exist, then it cannot be "in".
        /// </summary>
        private static RefKind GetEffectiveRefKind(
            int argIndex, 
            ImmutableArray<RefKind> argRefKindsOpt, 
            ImmutableArray<ParameterSymbol> parameters, 
            ImmutableArray<int> argsToParamsOpt, 
            ref ArrayBuilder<bool> refReadOnlyParametersMatchedWithArgs)
        {
            var effectiveRefKind = argRefKindsOpt.IsDefault ? RefKind.None : argRefKindsOpt[argIndex];
            if (effectiveRefKind == RefKind.None && argIndex < parameters.Length)
            {
                var paramIndex = argsToParamsOpt.IsDefault ? argIndex : argsToParamsOpt[argIndex];

                if (parameters[paramIndex].RefKind == RefKind.In)
                {
                    effectiveRefKind = RefKind.In;
                    refReadOnlyParametersMatchedWithArgs = refReadOnlyParametersMatchedWithArgs ?? ArrayBuilder<bool>.GetInstance(parameters.Length, fillWithValue: false);
                    refReadOnlyParametersMatchedWithArgs[paramIndex] = true;
                }
            }

            return effectiveRefKind;
        }

        /// <summary>
        /// Gets a "in" parameter for which there is no argument supplied, if such exists. 
        /// That indicates an optional "in" parameter. We treat it as an RValue passed by reference via a temporary.
        /// The effective scope of such variable is the immediately containing scope.
        /// </summary>
        private static ParameterSymbol TryGetUnmatchedRefReadOnlyParameterAndFreeMatchedArgs(ImmutableArray<ParameterSymbol> parameters, ref ArrayBuilder<bool> refReadOnlyParametersMatchedWithArgs)
        {
            try
            {
                if (!parameters.IsDefault)
                {
                    for (int i = 0; i < parameters.Length; i++)
                    {
                        var parameter = parameters[i];
                        if (parameter.IsParams)
                        {
                            break;
                        }

                        if (parameter.RefKind == RefKind.In &&
                            refReadOnlyParametersMatchedWithArgs?[i] != true &&
                            parameter.Type.IsByRefLikeType == false)
                        {
                            return parameter;
                        }
                    }
                }

                return null;
            }
            finally
            {
                refReadOnlyParametersMatchedWithArgs?.Free();
                // make sure noone uses it after.
                refReadOnlyParametersMatchedWithArgs = null;
            }
        }

        private static ErrorCode GetStandardCallEscapeError(bool checkingReceiver)
        {
            return checkingReceiver ? ErrorCode.ERR_EscapeCall2 : ErrorCode.ERR_EscapeCall;
        }

        private static void ReportReadonlyLocalError(SyntaxNode node, LocalSymbol local, BindValueKind kind, bool checkingReceiver, DiagnosticBag diagnostics)
        {
            Debug.Assert((object)local != null);
            Debug.Assert(kind != BindValueKind.RValue);

            MessageID cause;
            if (local.IsForEach)
            {
                cause = MessageID.IDS_FOREACHLOCAL;
            }
            else if (local.IsUsing)
            {
                cause = MessageID.IDS_USINGLOCAL;
            }
            else if (local.IsFixed)
            {
                cause = MessageID.IDS_FIXEDLOCAL;
            }
            else
            {
                Error(diagnostics, GetStandardLvalueError(kind), node);
                return;
            }

            if (kind == BindValueKind.AddressOf)
            {
                Error(diagnostics, ErrorCode.ERR_AddrOnReadOnlyLocal, node);
                return;
            }

            ErrorCode[] ReadOnlyLocalErrors =
            {
                ErrorCode.ERR_RefReadonlyLocalCause,
                ErrorCode.ERR_AssgReadonlyLocalCause,

                ErrorCode.ERR_RefReadonlyLocal2Cause,
                ErrorCode.ERR_AssgReadonlyLocal2Cause
            };

            int index = (checkingReceiver ? 2 : 0) + (RequiresRefOrOut(kind) ? 0 : 1);

            Error(diagnostics, ReadOnlyLocalErrors[index], node, local, cause.Localize());
        }

        static private ErrorCode GetThisLvalueError(BindValueKind kind)
        {
            switch (kind)
            {
                case BindValueKind.CompoundAssignment:
                case BindValueKind.Assignable:
                    return ErrorCode.ERR_AssgReadonlyLocal;

                case BindValueKind.RefOrOut:
                    return ErrorCode.ERR_RefReadonlyLocal;

                case BindValueKind.AddressOf:
                    return ErrorCode.ERR_AddrOnReadOnlyLocal;

                case BindValueKind.IncrementDecrement:
                    return ErrorCode.ERR_IncrementLvalueExpected;

                case BindValueKind.RefReturn:
                case BindValueKind.ReadonlyRef:
                    return ErrorCode.ERR_RefReturnThis;
            }

            throw ExceptionUtilities.UnexpectedValue(kind);
        }

        private static ErrorCode GetRangeLvalueError(BindValueKind kind)
        {
            switch (kind)
            {
                case BindValueKind.Assignable:
                case BindValueKind.CompoundAssignment:
                case BindValueKind.IncrementDecrement:
                    return ErrorCode.ERR_QueryRangeVariableReadOnly;

                case BindValueKind.AddressOf:
                    return ErrorCode.ERR_InvalidAddrOp;

                case BindValueKind.RefReturn:
                case BindValueKind.ReadonlyRef:
                    return ErrorCode.ERR_RefReturnRangeVariable;
            }

            if (RequiresReferenceToLocation(kind))
            {
                return ErrorCode.ERR_QueryOutRefRangeVariable;
            }

            throw ExceptionUtilities.UnexpectedValue(kind);
        }

        private static ErrorCode GetMethodGroupLvalueError(BindValueKind valueKind)
        {
            if (valueKind == BindValueKind.AddressOf)
            {
                return ErrorCode.ERR_InvalidAddrOp;
            }

            if (RequiresReferenceToLocation(valueKind))
            {
                return ErrorCode.ERR_RefReadonlyLocalCause;
            }

            // Cannot assign to 'W' because it is a 'method group'
            return ErrorCode.ERR_AssgReadonlyLocalCause;
        }

        static private ErrorCode GetStandardLvalueError(BindValueKind kind)
        {
            switch (kind)
            {
                case BindValueKind.CompoundAssignment:
                case BindValueKind.Assignable:
                    return ErrorCode.ERR_AssgLvalueExpected;

                case BindValueKind.AddressOf:
                    return ErrorCode.ERR_InvalidAddrOp;

                case BindValueKind.IncrementDecrement:
                    return ErrorCode.ERR_IncrementLvalueExpected;

                case BindValueKind.FixedReceiver:
                    return ErrorCode.ERR_FixedNeedsLvalue;

                case BindValueKind.RefReturn:
                case BindValueKind.ReadonlyRef:
                    return ErrorCode.ERR_RefReturnLvalueExpected;
            }

            if (RequiresReferenceToLocation(kind))
            {
                return ErrorCode.ERR_RefLvalueExpected;
            }

            throw ExceptionUtilities.UnexpectedValue(kind);
        }

        static private ErrorCode GetStandardRValueRefEscapeError(uint escapeTo)
        {
            if (escapeTo == Binder.ExternalScope)
            {
                return ErrorCode.ERR_RefReturnLvalueExpected;
            }

            return ErrorCode.ERR_EscapeOther;
        }

        private static void ReportReadOnlyFieldError(FieldSymbol field, SyntaxNode node, BindValueKind kind, bool checkingReceiver, DiagnosticBag diagnostics)
        {
            Debug.Assert((object)field != null);
            Debug.Assert(RequiresAssignableVariable(kind));
            Debug.Assert((object)field.Type != null);

            // It's clearer to say that the address can't be taken than to say that the field can't be modified
            // (even though the latter message gives more explanation of why).
            if (kind == BindValueKind.AddressOf)
            {
                Error(diagnostics, ErrorCode.ERR_InvalidAddrOp, node);
                return;
            }

            ErrorCode[] ReadOnlyErrors =
            {
                ErrorCode.ERR_RefReturnReadonly,
                ErrorCode.ERR_RefReadonly,
                ErrorCode.ERR_AssgReadonly,
                ErrorCode.ERR_RefReturnReadonlyStatic,
                ErrorCode.ERR_RefReadonlyStatic,
                ErrorCode.ERR_AssgReadonlyStatic,
                ErrorCode.ERR_RefReturnReadonly2,
                ErrorCode.ERR_RefReadonly2,
                ErrorCode.ERR_AssgReadonly2,
                ErrorCode.ERR_RefReturnReadonlyStatic2,
                ErrorCode.ERR_RefReadonlyStatic2,
                ErrorCode.ERR_AssgReadonlyStatic2
            };
            int index = (checkingReceiver ? 6 : 0) + (field.IsStatic ? 3 : 0) + (kind == BindValueKind.RefReturn ? 0 : (RequiresRefOrOut(kind) ? 1 : 2));
            if (checkingReceiver)
            {
                Error(diagnostics, ReadOnlyErrors[index], node, field);
            }
            else
            {
                Error(diagnostics, ReadOnlyErrors[index], node);
            }
        }

        private static void ReportReadOnlyError(Symbol symbol, SyntaxNode node, BindValueKind kind, bool checkingReceiver, DiagnosticBag diagnostics)
        {
            Debug.Assert((object)symbol != null);
            Debug.Assert(RequiresAssignableVariable(kind));

            // It's clearer to say that the address can't be taken than to say that the parameter can't be modified
            // (even though the latter message gives more explanation of why).
            if (kind == BindValueKind.AddressOf)
            {
                Error(diagnostics, ErrorCode.ERR_InvalidAddrOp, node);
                return;
            }

            var symbolKind = symbol.Kind.Localize();

            ErrorCode[] ReadOnlyErrors =
            {
                ErrorCode.ERR_RefReturnReadonlyNotField,
                ErrorCode.ERR_RefReadonlyNotField,
                ErrorCode.ERR_AssignReadonlyNotField,
                ErrorCode.ERR_RefReturnReadonlyNotField2,
                ErrorCode.ERR_RefReadonlyNotField2,
                ErrorCode.ERR_AssignReadonlyNotField2,
            };

            int index = (checkingReceiver ? 3 : 0) + (kind == BindValueKind.RefReturn ? 0 : (RequiresRefOrOut(kind) ? 1 : 2));
            Error(diagnostics, ReadOnlyErrors[index], node, symbolKind, symbol);
        }

        /// <summary>
        /// Checks whether given expression can escape from the current scope to the `escapeTo`
        /// In a case if it cannot a bad expression is returned and diagnostics is produced.
        /// </summary>
        internal BoundExpression ValidateEscape(BoundExpression expr, uint escapeTo, bool isByRef, DiagnosticBag diagnostics)
        {
            if (isByRef)
            {
                if (CheckRefEscape(expr.Syntax, expr, this.LocalScopeDepth, escapeTo, checkingReceiver: false, diagnostics: diagnostics))
                {
                    return expr;
                }
            }
            else
            {
                if (CheckValEscape(expr.Syntax, expr, this.LocalScopeDepth, escapeTo, checkingReceiver: false, diagnostics: diagnostics))
                {
                    return expr;
                }
            }

            return ToBadExpression(expr);
        }

        /// <summary>
        /// Computes the widest scope depth to which the given expression can escape by reference.
        /// 
        /// NOTE: in a case if expression cannot be passed by an alias (RValue and similar), the ref-escape is scopeOfTheContainingExpression
        ///       There are few cases where RValues are permitted to be passed by reference which implies that a temporary local proxy is passed instead.
        ///       We reflect such behavior by constraining the escape value to the narrowest scope possible. 
        /// </summary>
        internal static uint GetRefEscape(BoundExpression expr, uint scopeOfTheContainingExpression)
        {
            // cannot infer anything from errors
            if (expr.HasAnyErrors)
            {
                return Binder.ExternalScope;
            }

            // cannot infer anything from Void (broken code)
            if (expr.Type?.GetSpecialTypeSafe() == SpecialType.System_Void)
            {
                return Binder.ExternalScope;
            }

            // constants/literals cannot ref-escape current scope
            if (expr.ConstantValue != null)
            {
                return scopeOfTheContainingExpression;
            }

            // cover case that cannot refer to local state
            // otherwise default to current scope (RValues, etc)
            switch (expr.Kind)
            {
                case BoundKind.ArrayAccess:
                case BoundKind.PointerIndirectionOperator:
                case BoundKind.PointerElementAccess:
                    // array elements and pointer dereferencing are readwrite variables
                    return Binder.ExternalScope;

                case BoundKind.RefValueOperator:
                    // The undocumented __refvalue(tr, T) expression results in an lvalue of type T.
                    // for compat reasons it is not ref-returnable (since TypedReference is not val-returnable)
                    // it can, however, ref-escape to any other level (since TypedReference can val-escape to any other level)
                    return Binder.TopLevelScope;

                case BoundKind.DiscardExpression:
                    // same as write-only byval local
                    break;

                case BoundKind.DynamicMemberAccess:
                case BoundKind.DynamicIndexerAccess:
                    // dynamic expressions can be read and written to
                    // can even be passed by reference (which is implemented via a temp)
                    // it is not valid to escape them by reference though, so treat them as RValues here
                    break;

                case BoundKind.Parameter:
                    var parameter = ((BoundParameter)expr).ParameterSymbol;

                    // byval parameters can escape to method's top level.
                    // others can be escape further, unless they are ref-like.
                    // NOTE: "method" here means nearst containing method, lambda or nested method
                    return parameter.RefKind == RefKind.None || parameter.Type?.IsByRefLikeType == true ?
                        Binder.TopLevelScope :
                        Binder.ExternalScope;

                case BoundKind.Local:
                    return ((BoundLocal)expr).LocalSymbol.RefEscapeScope;

                case BoundKind.ThisReference:
                    var thisref = (BoundThisReference)expr;

                    // "this" is an RValue, unless in a struct.
                    if (!thisref.Type.IsValueType)
                    {
                        break;
                    }

                    //"this" is not returnable by reference in a struct.
                    // can ref escape to any other level
                    return Binder.TopLevelScope;

                case BoundKind.ConditionalOperator:
                    var conditional = (BoundConditionalOperator)expr;

                    if (conditional.IsByRef)
                    {
                        // ref conditional defers to its operands
                        return Math.Max(GetRefEscape(conditional.Consequence, scopeOfTheContainingExpression),
                                        GetRefEscape(conditional.Alternative, scopeOfTheContainingExpression));
                    }

                    // otherwise it is an RValue
                    break;

                case BoundKind.FieldAccess:
                    var fieldAccess = (BoundFieldAccess)expr;
                    var fieldSymbol = fieldAccess.FieldSymbol;

                    // fields that are static or belong to reference types can ref escape anywhere
                    if (fieldSymbol.IsStatic || fieldSymbol.ContainingType.IsReferenceType)
                    {
                        return Binder.ExternalScope;
                    }

                    // for other fields defer to the receiver.
                    return GetRefEscape(fieldAccess.ReceiverOpt, scopeOfTheContainingExpression);

                case BoundKind.EventAccess:
                    var eventAccess = (BoundEventAccess)expr;
                    if (!eventAccess.IsUsableAsField)
                    {
                        // not field-like events are RValues
                        break;
                    }

                    var eventSymbol = eventAccess.EventSymbol;

                    // field-like events that are static or belong to reference types can ref escape anywhere
                    if (eventSymbol.IsStatic || eventSymbol.ContainingType.IsReferenceType)
                    {
                        return Binder.ExternalScope;
                    }

                    // for other events defer to the receiver.
                    return GetRefEscape(eventAccess.ReceiverOpt, scopeOfTheContainingExpression);

                case BoundKind.Call:
                    var call = (BoundCall)expr;

                    var methodSymbol = call.Method;
                    if (methodSymbol.RefKind == RefKind.None)
                    {
                        break;
                    }

                    return GetInvocationEscapeScope(
                        call.Method,
                        call.ReceiverOpt,
                        methodSymbol.Parameters,
                        call.Arguments,
                        call.ArgumentRefKindsOpt,
                        call.ArgsToParamsOpt,
                        scopeOfTheContainingExpression,
                        isRefEscape: true);

                case BoundKind.IndexerAccess:
                    var indexerAccess = (BoundIndexerAccess)expr;
                    var indexerSymbol = indexerAccess.Indexer;

                    return GetInvocationEscapeScope(
                        indexerSymbol,
                        indexerAccess.ReceiverOpt,
                        indexerSymbol.Parameters,
                        indexerAccess.Arguments,
                        indexerAccess.ArgumentRefKindsOpt,
                        indexerAccess.ArgsToParamsOpt,
                        scopeOfTheContainingExpression,
                        isRefEscape: true);

                case BoundKind.PropertyAccess:
                    var propertyAccess = (BoundPropertyAccess)expr;

                    // not passing any arguments/parameters
                    return GetInvocationEscapeScope(
                        propertyAccess.PropertySymbol,
                        propertyAccess.ReceiverOpt,
                        default,
                        default,
                        default,
                        default,
                        scopeOfTheContainingExpression,
                        isRefEscape: true);
            }

            // At this point we should have covered all the possible cases for anything that is not a strict RValue.
            return scopeOfTheContainingExpression;
        }

        /// <summary>
        /// A counterpart to the GetRefEscape, which validates if given escape demand can be met by the expression.
        /// The result indicates whether the escape is possible. 
        /// Additionally, the method emits diagnostics (possibly more than one, recursively) that would help identify the cause for the failure.
        /// </summary>
        internal static bool CheckRefEscape(SyntaxNode node, BoundExpression expr, uint escapeFrom, uint escapeTo, bool checkingReceiver, DiagnosticBag diagnostics)
        {
            Debug.Assert(!checkingReceiver || expr.Type.IsValueType || expr.Type.IsTypeParameter());

            if (escapeTo >= escapeFrom)
            {
                // escaping to same or narrower scope is ok.
                return true;
            }

            if (expr.HasAnyErrors)
            {
                // already an error
                return true;
            }

            // void references cannot escape (error should be reported somewhere)
            if (expr.Type?.GetSpecialTypeSafe() == SpecialType.System_Void)
            {
                return true;
            }

            // references to constants/literals cannot escape higher.
            if (expr.ConstantValue != null)
            {
                Error(diagnostics, GetStandardRValueRefEscapeError(escapeTo), node);
                return false;
            }

            switch (expr.Kind)
            {
                case BoundKind.ArrayAccess:
                case BoundKind.PointerIndirectionOperator:
                case BoundKind.PointerElementAccess:
                    // array elements and pointer dereferencing are readwrite variables
                    return true;

                case BoundKind.RefValueOperator:
                    // The undocumented __refvalue(tr, T) expression results in an lvalue of type T.
                    // for compat reasons it is not ref-returnable (since TypedReference is not val-returnable)
                    if (escapeTo == Binder.ExternalScope)
                    {
                        break;
                    }

                    // it can, however, ref-escape to any other level (since TypedReference can val-escape to any other level)
                    return true;

                case BoundKind.DiscardExpression:
                    // same as write-only byval local
                    break;

                case BoundKind.DynamicMemberAccess:
                case BoundKind.DynamicIndexerAccess:
                    // dynamic expressions can be read and written to
                    // can even be passed by reference (which is implemented via a temp)
                    // it is not valid to escape them by reference though.
                    break;

                case BoundKind.Parameter:
                    var parameter = (BoundParameter)expr;
                    return CheckParameterRefEscape(node, parameter, escapeTo, checkingReceiver, diagnostics);

                case BoundKind.Local:
                    var local = (BoundLocal)expr;
                    return CheckLocalRefEscape(node, local, escapeTo, checkingReceiver, diagnostics);

                case BoundKind.ThisReference:
                    var thisref = (BoundThisReference)expr;

                    // "this" is an RValue, unless in a struct.
                    if (!thisref.Type.IsValueType)
                    {
                        break;
                    }

                    //"this" is not returnable by reference in a struct.
                    if (escapeTo == Binder.ExternalScope)
                    {
                        Error(diagnostics, ErrorCode.ERR_RefReturnStructThis, node, ThisParameterSymbol.SymbolName);
                        return false;
                    }

                    // can ref escape to any other level
                    return true;

                case BoundKind.ConditionalOperator:
                    var conditional = (BoundConditionalOperator)expr;

                    if (conditional.IsByRef)
                    {
                        return CheckRefEscape(conditional.Consequence.Syntax, conditional.Consequence, escapeFrom, escapeTo, checkingReceiver: false, diagnostics: diagnostics) &&
                               CheckRefEscape(conditional.Alternative.Syntax, conditional.Alternative, escapeFrom, escapeTo, checkingReceiver: false, diagnostics: diagnostics);
                    }

                    // report standard lvalue error
                    break;

                case BoundKind.FieldAccess:
                    var fieldAccess = (BoundFieldAccess)expr;
                    return CheckFieldRefEscape(node, fieldAccess, escapeFrom, escapeTo, checkingReceiver, diagnostics);

                case BoundKind.EventAccess:
                    var eventAccess = (BoundEventAccess)expr;
                    if (!eventAccess.IsUsableAsField)
                    {
                        // not field-like events are RValues
                        break;
                    }

                    return CheckFieldLikeEventRefEscape(node, eventAccess, escapeFrom, escapeTo, checkingReceiver, diagnostics);

                case BoundKind.Call:
                    var call = (BoundCall)expr;

                    var methodSymbol = call.Method;
                    if (methodSymbol.RefKind == RefKind.None)
                    {
                        break;
                    }

                    return CheckInvocationEscape(
                        call.Syntax,
                        methodSymbol,
                        call.ReceiverOpt,
                        methodSymbol.Parameters,
                        call.Arguments,
                        call.ArgumentRefKindsOpt,
                        call.ArgsToParamsOpt,
                        checkingReceiver,
                        escapeFrom,
                        escapeTo,
                        diagnostics,
                        isRefEscape: true);

                case BoundKind.IndexerAccess:
                    var indexerAccess = (BoundIndexerAccess)expr;
                    var indexerSymbol = indexerAccess.Indexer;

                    if (indexerSymbol.RefKind == RefKind.None)
                    {
                        break;
                    }

                    return CheckInvocationEscape(
                        indexerAccess.Syntax,
                        indexerSymbol,
                        indexerAccess.ReceiverOpt,
                        indexerSymbol.Parameters,
                        indexerAccess.Arguments,
                        indexerAccess.ArgumentRefKindsOpt,
                        indexerAccess.ArgsToParamsOpt,
                        checkingReceiver,
                        escapeFrom,
                        escapeTo,
                        diagnostics,
                        isRefEscape: true);

                case BoundKind.PropertyAccess:
                    var propertyAccess = (BoundPropertyAccess)expr;
                    var propertySymbol = propertyAccess.PropertySymbol;

                    if (propertySymbol.RefKind == RefKind.None)
                    {
                        break;
                    }

                    // not passing any arguments/parameters
                    return CheckInvocationEscape(
                        propertyAccess.Syntax,
                        propertySymbol,
                        propertyAccess.ReceiverOpt,
                        default,
                        default,
                        default,
                        default,
                        checkingReceiver,
                        escapeFrom,
                        escapeTo,
                        diagnostics,
                        isRefEscape: true);
            }

            // At this point we should have covered all the possible cases for anything that is not a strict RValue.
            Error(diagnostics, GetStandardRValueRefEscapeError(escapeTo), node);
            return false;
        }

        /// <summary>
        /// Computes the widest scope depth to which the given expression can escape by value.
        /// 
        /// NOTE: unless the type of expression is ref-like, the result is Binder.ExternalScope since ordinary values can always be returned from methods. 
        /// </summary>
        internal static uint GetValEscape(BoundExpression expr, uint scopeOfTheContainingExpression)
        {
            // cannot infer anything from errors
            if (expr.HasAnyErrors)
            {
                return Binder.ExternalScope;
            }

            // constants/literals cannot refer to local state
            if (expr.ConstantValue != null)
            {
                return Binder.ExternalScope;
            }

            // to have local-referring values an expression must have a ref-like type
            if (expr.Type?.IsByRefLikeType != true)
            {
                return Binder.ExternalScope;
            }

            // cover case that can refer to local state
            // otherwise default to ExternalScope (ordinary values)
            switch (expr.Kind)
            {
                case BoundKind.DefaultExpression:
                case BoundKind.Parameter:
                case BoundKind.ThisReference:
                    // always returnable
                    return Binder.ExternalScope;

                case BoundKind.MakeRefOperator:
                case BoundKind.RefValueOperator:
                    // for compat reasons
                    // NB: it also means can`t assign stackalloc spans to a __refvalue
                    //     we are ok with that.
                    return Binder.ExternalScope;

                case BoundKind.DiscardExpression:
                    // same as uninitialized local
                    return Binder.ExternalScope;

                case BoundKind.Local:
                    return ((BoundLocal)expr).LocalSymbol.ValEscapeScope;

                case BoundKind.StackAllocArrayCreation:
                case BoundKind.ConvertedStackAllocExpression:
                    return Binder.TopLevelScope;

                case BoundKind.ConditionalOperator:
                    var conditional = (BoundConditionalOperator)expr;

                    var consEscape = GetValEscape(conditional.Consequence, scopeOfTheContainingExpression);

                    if (conditional.IsByRef)
                    {
                        // ref conditional defers to one operand. 
                        // the other one is the same or we will be reporting errors anyways.
                        return consEscape;
                    }
                    
                    // val conditional gets narrowest of its operands
                    return Math.Max(consEscape,
                                    GetValEscape(conditional.Alternative, scopeOfTheContainingExpression));

                case BoundKind.FieldAccess:
                    var fieldAccess = (BoundFieldAccess)expr;
                    var fieldSymbol = fieldAccess.FieldSymbol;

                    Debug.Assert(!fieldSymbol.IsStatic && fieldSymbol.ContainingType.IsByRefLikeType);

                    // for ref-like fields defer to the receiver.
                    return GetValEscape(fieldAccess.ReceiverOpt, scopeOfTheContainingExpression);

                case BoundKind.Call:
                    var call = (BoundCall)expr;

                    return GetInvocationEscapeScope(
                        call.Method,
                        call.ReceiverOpt,
                        call.Method.Parameters,
                        call.Arguments,
                        call.ArgumentRefKindsOpt,
                        call.ArgsToParamsOpt,
                        scopeOfTheContainingExpression,
                        isRefEscape: false);

                case BoundKind.IndexerAccess:
                    var indexerAccess = (BoundIndexerAccess)expr;
                    var indexerSymbol = indexerAccess.Indexer;

                    return GetInvocationEscapeScope(
                        indexerSymbol,
                        indexerAccess.ReceiverOpt,
                        indexerSymbol.Parameters,
                        indexerAccess.Arguments,
                        indexerAccess.ArgumentRefKindsOpt,
                        indexerAccess.ArgsToParamsOpt,
                        scopeOfTheContainingExpression,
                        isRefEscape: false);

                case BoundKind.PropertyAccess:
                    var propertyAccess = (BoundPropertyAccess)expr;

                    // not passing any arguments/parameters
                    return GetInvocationEscapeScope(
                        propertyAccess.PropertySymbol,
                        propertyAccess.ReceiverOpt,
                        default,
                        default,
                        default,
                        default,
                        scopeOfTheContainingExpression,
                        isRefEscape: false);

                case BoundKind.ObjectCreationExpression:
                    var objectCreation = (BoundObjectCreationExpression)expr;
                    var constructorSymbol = objectCreation.Constructor;

                    var escape = GetInvocationEscapeScope(
                        constructorSymbol,
                        null,
                        constructorSymbol.Parameters,
                        objectCreation.Arguments,
                        objectCreation.ArgumentRefKindsOpt,
                        objectCreation.ArgsToParamsOpt,
                        scopeOfTheContainingExpression,
                        isRefEscape: false);

                    var initializerOpt = objectCreation.InitializerExpressionOpt;
                    if (initializerOpt != null)
                    {
                        escape = Math.Max(escape, GetValEscape(initializerOpt, scopeOfTheContainingExpression));
                    }

                    return escape;

                case BoundKind.UnaryOperator:
                    return GetValEscape(((BoundUnaryOperator)expr).Operand, scopeOfTheContainingExpression);

                case BoundKind.Conversion:
                    var conversion = (BoundConversion)expr;
                    Debug.Assert(conversion.ConversionKind != ConversionKind.StackAllocToSpanType, "StackAllocToSpanType unexpected");

                    return GetValEscape(conversion.Operand, scopeOfTheContainingExpression);

                case BoundKind.AssignmentOperator:
                    return GetValEscape(((BoundAssignmentOperator)expr).Right, scopeOfTheContainingExpression);

                case BoundKind.IncrementOperator:
                    return GetValEscape(((BoundIncrementOperator)expr).Operand, scopeOfTheContainingExpression);

                case BoundKind.CompoundAssignmentOperator:
                    var compound = (BoundCompoundAssignmentOperator)expr;

                    return Math.Max(GetValEscape(compound.Left, scopeOfTheContainingExpression),
                                    GetValEscape(compound.Right, scopeOfTheContainingExpression));

                case BoundKind.BinaryOperator:
                    var binary = (BoundBinaryOperator)expr;

                    return Math.Max(GetValEscape(binary.Left, scopeOfTheContainingExpression),
                                    GetValEscape(binary.Right, scopeOfTheContainingExpression));

                case BoundKind.UserDefinedConditionalLogicalOperator:
                    var uo = (BoundUserDefinedConditionalLogicalOperator)expr;

                    return Math.Max(GetValEscape(uo.Left, scopeOfTheContainingExpression),
                                    GetValEscape(uo.Right, scopeOfTheContainingExpression));

                case BoundKind.QueryClause:
                    return GetValEscape(((BoundQueryClause)expr).Value, scopeOfTheContainingExpression);

                case BoundKind.RangeVariable:
                    return GetValEscape(((BoundRangeVariable)expr).Value, scopeOfTheContainingExpression);

                case BoundKind.ObjectInitializerExpression:
                    var initExpr = (BoundObjectInitializerExpression)expr;
                    return GetValEscapeOfObjectInitializer(initExpr, scopeOfTheContainingExpression);

                case BoundKind.CollectionInitializerExpression:
                    var colExpr = (BoundCollectionInitializerExpression)expr;
                    return GetValEscape(colExpr.Initializers, scopeOfTheContainingExpression);

                case BoundKind.CollectionElementInitializer:
                    var colElement = (BoundCollectionElementInitializer)expr;
                    return GetValEscape(colElement.Arguments, scopeOfTheContainingExpression);
                                
                case BoundKind.ObjectInitializerMember:
                    // this node generally makes no sense outside of the context of containing initializer
                    // however binder uses it as a placeholder when binding assignments inside an object initializer
                    // just say it does not escape anywhere, so that we do not get false errors.
                    return scopeOfTheContainingExpression;

                case BoundKind.ImplicitReceiver:
                    // binder uses this as a placeholder when binding members inside an object initializer
                    // just say it does not escape anywhere, so that we do not get false errors.
                    return scopeOfTheContainingExpression;

                default:
                    throw ExceptionUtilities.UnexpectedValue($"{expr.Kind} expression of {expr.Type} type");
            }
        }

        private static uint GetValEscapeOfObjectInitializer(BoundObjectInitializerExpression initExpr, uint scopeOfTheContainingExpression)
        {
            var result = Binder.ExternalScope;
            foreach (var expression in initExpr.Initializers)
            {
                if (expression.Kind == BoundKind.AssignmentOperator)
                {
                    var assignment = (BoundAssignmentOperator)expression;
                    result = Math.Max(result, GetValEscape(assignment.Right, scopeOfTheContainingExpression));

                    var left = (BoundObjectInitializerMember)assignment.Left;
                    result = Math.Max(result, GetValEscape(left.Arguments, scopeOfTheContainingExpression));
                }
                else
                {
                    result = Math.Max(result, GetValEscape(expression, scopeOfTheContainingExpression));
                }
            }

            return result;
        }

        private static uint GetValEscape(ImmutableArray<BoundExpression> expressions, uint scopeOfTheContainingExpression)
        {
            var result = Binder.ExternalScope;
            foreach (var expression in expressions)
            {
                result = Math.Max(result, GetValEscape(expression, scopeOfTheContainingExpression));
            }

            return result;
        }

        /// <summary>
        /// A counterpart to the GetValEscape, which validates if given escape demand can be met by the expression.
        /// The result indicates whether the escape is possible. 
        /// Additionally, the method emits diagnostics (possibly more than one, recursively) that would help identify the cause for the failure.
        /// </summary>
        internal static bool CheckValEscape(SyntaxNode node, BoundExpression expr, uint escapeFrom, uint escapeTo, bool checkingReceiver, DiagnosticBag diagnostics)
        {
            Debug.Assert(!checkingReceiver || expr.Type.IsValueType || expr.Type.IsTypeParameter());

            if (escapeTo >= escapeFrom)
            {
                // escaping to same or narrower scope is ok.
                return true;
            }

            // cannot infer anything from errors
            if (expr.HasAnyErrors)
            {
                return true;
            }

            // constants/literals cannot refer to local state
            if (expr.ConstantValue != null)
            {
                return true;
            }

            // to have local-referring values an expression must have a ref-like type
            if (expr.Type?.IsByRefLikeType != true)
            {
                return true;
            }

            switch (expr.Kind)
            {
                case BoundKind.DefaultExpression:
                case BoundKind.Parameter:
                case BoundKind.ThisReference:
                    // always returnable
                    return true;

                case BoundKind.MakeRefOperator:
                case BoundKind.RefValueOperator:
                    // for compat reasons
                    return true;

                case BoundKind.DiscardExpression:
                    // same as uninitialized local
                    return true;

                case BoundKind.Local:
                    var localSymbol = ((BoundLocal)expr).LocalSymbol;
                    if (localSymbol.ValEscapeScope > escapeTo)
                    {
                        Error(diagnostics, ErrorCode.ERR_EscapeLocal, node, localSymbol);
                        return false;
                    }
                    return true;

                case BoundKind.StackAllocArrayCreation:
                case BoundKind.ConvertedStackAllocExpression:
                    if (escapeTo < Binder.TopLevelScope)
                    {
                        Error(diagnostics, ErrorCode.ERR_EscapeStackAlloc, node, expr.Type);
                        return false;
                    }
                    return true;

                case BoundKind.ConditionalOperator:
                    var conditional = (BoundConditionalOperator)expr;

                    var consValid = CheckValEscape(conditional.Consequence.Syntax, conditional.Consequence, escapeFrom, escapeTo, checkingReceiver: false, diagnostics: diagnostics);

                    if (!consValid || conditional.IsByRef)
                    {
                        // ref conditional defers to one operand. 
                        // the other one is the same or we will be reporting errors anyways.
                        return consValid;
                    }

                    return CheckValEscape(conditional.Alternative.Syntax, conditional.Alternative, escapeFrom, escapeTo, checkingReceiver: false, diagnostics: diagnostics);

                case BoundKind.FieldAccess:
                    var fieldAccess = (BoundFieldAccess)expr;
                    var fieldSymbol = fieldAccess.FieldSymbol;

                    Debug.Assert(!fieldSymbol.IsStatic && fieldSymbol.ContainingType.IsByRefLikeType);

                    // for ref-like fields defer to the receiver.
                    return CheckValEscape(node, fieldAccess.ReceiverOpt, escapeFrom, escapeTo, true, diagnostics);

                case BoundKind.Call:
                    var call = (BoundCall)expr;
                    var methodSymbol = call.Method;

                    return CheckInvocationEscape(
                        call.Syntax,
                        methodSymbol,
                        call.ReceiverOpt,
                        methodSymbol.Parameters,
                        call.Arguments,
                        call.ArgumentRefKindsOpt,
                        call.ArgsToParamsOpt,
                        checkingReceiver,
                        escapeFrom,
                        escapeTo,
                        diagnostics,
                        isRefEscape: false);

                case BoundKind.IndexerAccess:
                    var indexerAccess = (BoundIndexerAccess)expr;
                    var indexerSymbol = indexerAccess.Indexer;

                    return CheckInvocationEscape(
                        indexerAccess.Syntax,
                        indexerSymbol,
                        indexerAccess.ReceiverOpt,
                        indexerSymbol.Parameters,
                        indexerAccess.Arguments,
                        indexerAccess.ArgumentRefKindsOpt,
                        indexerAccess.ArgsToParamsOpt,
                        checkingReceiver,
                        escapeFrom,
                        escapeTo,
                        diagnostics,
                        isRefEscape: false);

                case BoundKind.PropertyAccess:
                    var propertyAccess = (BoundPropertyAccess)expr;

                    // not passing any arguments/parameters
                    return CheckInvocationEscape(
                        propertyAccess.Syntax,
                        propertyAccess.PropertySymbol,
                        propertyAccess.ReceiverOpt,
                        default,
                        default,
                        default,
                        default,
                        checkingReceiver,
                        escapeFrom,
                        escapeTo,
                        diagnostics,
                        isRefEscape: false);

                case BoundKind.ObjectCreationExpression:
                    var objectCreation = (BoundObjectCreationExpression)expr;
                    var constructorSymbol = objectCreation.Constructor;

                    var escape = CheckInvocationEscape(
                        objectCreation.Syntax,
                        constructorSymbol,
                        null,
                        constructorSymbol.Parameters,
                        objectCreation.Arguments,
                        objectCreation.ArgumentRefKindsOpt,
                        objectCreation.ArgsToParamsOpt,
                        checkingReceiver,
                        escapeFrom,
                        escapeTo,
                        diagnostics,
                        isRefEscape: false);

                    var initializerExpr = objectCreation.InitializerExpressionOpt;
                    if (initializerExpr != null)
                    {
                        escape = escape && 
                            CheckValEscape(
                                initializerExpr.Syntax, 
                                initializerExpr, 
                                escapeFrom, 
                                escapeTo, 
                                checkingReceiver:false, 
                                diagnostics:diagnostics);
                    }

                    return escape;

                case BoundKind.UnaryOperator:
                    var unary = (BoundUnaryOperator)expr;
                    return CheckValEscape(node, unary.Operand, escapeFrom, escapeTo, checkingReceiver: false, diagnostics: diagnostics);

                case BoundKind.Conversion:
                    var conversion = (BoundConversion)expr;
                    Debug.Assert(conversion.ConversionKind != ConversionKind.StackAllocToSpanType, "StackAllocToSpanType unexpected");
                    return CheckValEscape(node, conversion.Operand, escapeFrom, escapeTo, checkingReceiver: false, diagnostics: diagnostics);

                case BoundKind.AssignmentOperator:
                    var assignment = (BoundAssignmentOperator)expr;
                    return CheckValEscape(node, assignment.Right, escapeFrom, escapeTo, checkingReceiver: false, diagnostics: diagnostics);

                case BoundKind.IncrementOperator:
                    var increment = (BoundIncrementOperator)expr;
                    return CheckValEscape(node, increment.Operand, escapeFrom, escapeTo, checkingReceiver: false, diagnostics: diagnostics);

                case BoundKind.CompoundAssignmentOperator:
                    var compound = (BoundCompoundAssignmentOperator)expr;

                    return CheckValEscape(compound.Left.Syntax, compound.Left, escapeFrom, escapeTo, checkingReceiver: false, diagnostics: diagnostics) &&
                           CheckValEscape(compound.Right.Syntax, compound.Right, escapeFrom, escapeTo, checkingReceiver: false, diagnostics: diagnostics);

                case BoundKind.BinaryOperator:
                    var binary = (BoundBinaryOperator)expr;

                    return CheckValEscape(binary.Left.Syntax, binary.Left, escapeFrom, escapeTo, checkingReceiver: false, diagnostics: diagnostics) &&
                           CheckValEscape(binary.Right.Syntax, binary.Right, escapeFrom, escapeTo, checkingReceiver: false, diagnostics: diagnostics);

                case BoundKind.UserDefinedConditionalLogicalOperator:
                    var uo = (BoundUserDefinedConditionalLogicalOperator)expr;

                    return CheckValEscape(uo.Left.Syntax, uo.Left, escapeFrom, escapeTo, checkingReceiver: false, diagnostics: diagnostics) &&
                           CheckValEscape(uo.Right.Syntax, uo.Right, escapeFrom, escapeTo, checkingReceiver: false, diagnostics: diagnostics);

                case BoundKind.QueryClause:
                    var clauseValue = ((BoundQueryClause)expr).Value;
                    return CheckValEscape(clauseValue.Syntax, clauseValue, escapeFrom, escapeTo, checkingReceiver: false, diagnostics: diagnostics);

                case BoundKind.RangeVariable:  
                    var variableValue = ((BoundRangeVariable)expr).Value;
                    return CheckValEscape(variableValue.Syntax, variableValue, escapeFrom, escapeTo, checkingReceiver: false, diagnostics: diagnostics);

                case BoundKind.ObjectInitializerExpression:
                    var initExpr = (BoundObjectInitializerExpression)expr;
                    return CheckValEscapeOfObjectInitializer(initExpr, escapeFrom, escapeTo, diagnostics);

                // this would be correct implementation for CollectionInitializerExpression 
                // however it is unclear if it is reachable since the initialized type must implement IEnumerable
                case BoundKind.CollectionInitializerExpression:
                    var colExpr = (BoundCollectionInitializerExpression)expr;
                    return CheckValEscape(colExpr.Initializers, escapeFrom, escapeTo, diagnostics);

                // this would be correct implementation for CollectionElementInitializer 
                // however it is unclear if it is reachable since the initialized type must implement IEnumerable
                case BoundKind.CollectionElementInitializer:
                    var colElement = (BoundCollectionElementInitializer)expr;
                    return CheckValEscape(colElement.Arguments, escapeFrom, escapeTo, diagnostics);

                default:
                    throw ExceptionUtilities.UnexpectedValue($"{expr.Kind} expression of {expr.Type} type");

                #region "cannot produce ref-like values"
//                case BoundKind.ThrowExpression:
//                case BoundKind.PointerIndirectionOperator:
//                case BoundKind.PointerElementAccess:
//                case BoundKind.ArgListOperator:
//                case BoundKind.ArgList:
//                case BoundKind.RefTypeOperator:
//                case BoundKind.AddressOfOperator:
//                case BoundKind.AsOperator:
//                case BoundKind.TypeOfOperator:
//                case BoundKind.ArrayAccess:
//                case BoundKind.NullCoalescingOperator:
//                case BoundKind.AwaitExpression:
//                case BoundKind.IsOperator:
//                case BoundKind.SizeOfOperator:
//                case BoundKind.DynamicMemberAccess:
//                case BoundKind.DynamicInvocation:
//                case BoundKind.NewT:
//                case BoundKind.DelegateCreationExpression:
//                case BoundKind.ArrayCreation:
//                case BoundKind.AnonymousObjectCreationExpression:
//                case BoundKind.NameOfOperator:
//                case BoundKind.InterpolatedString:
//                case BoundKind.StringInsert:
//                case BoundKind.TupleLiteral:
//                case BoundKind.ConvertedTupleLiteral:
//                case BoundKind.DynamicIndexerAccess:
//                case BoundKind.Lambda:
//                case BoundKind.DynamicObjectCreationExpression:
//                case BoundKind.NoPiaObjectCreationExpression:
//                case BoundKind.BaseReference:
//                case BoundKind.Literal:
//                case BoundKind.ConditionalAccess:
//                case BoundKind.IsPatternExpression:
//                case BoundKind.DeconstructionAssignmentOperator:
//                case BoundKind.EventAccess:

                #endregion

                #region "not expression that can produce a value"
//                case BoundKind.FieldEqualsValue:
//                case BoundKind.PropertyEqualsValue:
//                case BoundKind.ParameterEqualsValue:
//                case BoundKind.NamespaceExpression:
//                case BoundKind.TypeExpression:
//                case BoundKind.BadStatement:
//                case BoundKind.MethodDefIndex:
//                case BoundKind.SourceDocumentIndex:
//                case BoundKind.ArgList:
//                case BoundKind.ArgListOperator:
//                case BoundKind.Block:
//                case BoundKind.Scope:
//                case BoundKind.NoOpStatement:
//                case BoundKind.ReturnStatement:
//                case BoundKind.YieldReturnStatement:
//                case BoundKind.YieldBreakStatement:
//                case BoundKind.ThrowStatement:
//                case BoundKind.ExpressionStatement:
//                case BoundKind.SwitchStatement:
//                case BoundKind.SwitchSection:
//                case BoundKind.SwitchLabel:
//                case BoundKind.BreakStatement:
//                case BoundKind.LocalFunctionStatement:
//                case BoundKind.ContinueStatement:
//                case BoundKind.PatternSwitchStatement:
//                case BoundKind.PatternSwitchSection:
//                case BoundKind.PatternSwitchLabel:
//                case BoundKind.IfStatement:
//                case BoundKind.DoStatement:
//                case BoundKind.WhileStatement:
//                case BoundKind.ForStatement:
//                case BoundKind.ForEachStatement:
//                case BoundKind.ForEachDeconstructStep:
//                case BoundKind.UsingStatement:
//                case BoundKind.FixedStatement:
//                case BoundKind.LockStatement:
//                case BoundKind.TryStatement:
//                case BoundKind.CatchBlock:
//                case BoundKind.LabelStatement:
//                case BoundKind.GotoStatement:
//                case BoundKind.LabeledStatement:
//                case BoundKind.Label:
//                case BoundKind.StatementList:
//                case BoundKind.ConditionalGoto:
//                case BoundKind.LocalDeclaration:
//                case BoundKind.MultipleLocalDeclarations:
//                case BoundKind.ArrayInitialization:
//                case BoundKind.AnonymousPropertyDeclaration:
//                case BoundKind.MethodGroup:
//                case BoundKind.PropertyGroup:
//                case BoundKind.EventAssignmentOperator:
//                case BoundKind.Attribute:
//                case BoundKind.FixedLocalCollectionInitializer:
//                case BoundKind.DynamicObjectInitializerMember:
//                case BoundKind.DynamicCollectionElementInitializer:
//                case BoundKind.ImplicitReceiver:
//                case BoundKind.FieldInitializer:
//                case BoundKind.GlobalStatementInitializer:
//                case BoundKind.TypeOrInstanceInitializers:
//                case BoundKind.DeclarationPattern:
//                case BoundKind.ConstantPattern:
//                case BoundKind.WildcardPattern:
//                case BoundKind.DeconstructValuePlaceholder:

                #endregion

                #region "not found as an operand in no-error unlowered bound tree"
//                case BoundKind.MaximumMethodDefIndex:
//                case BoundKind.InstrumentationPayloadRoot:
//                case BoundKind.ModuleVersionId:
//                case BoundKind.ModuleVersionIdString:
//                case BoundKind.Dup:
//                case BoundKind.TypeOrValueExpression:
//                case BoundKind.BadExpression:
//                case BoundKind.ArrayLength:
//                case BoundKind.MethodInfo:
//                case BoundKind.FieldInfo:
//                case BoundKind.SequencePoint:
//                case BoundKind.SequencePointExpression:
//                case BoundKind.SequencePointWithSpan:
//                case BoundKind.StateMachineScope:
//                case BoundKind.ConditionalReceiver:
//                case BoundKind.ComplexConditionalReceiver:
//                case BoundKind.PreviousSubmissionReference:
//                case BoundKind.HostObjectMemberReference:
//                case BoundKind.UnboundLambda:
//                case BoundKind.LoweredConditionalAccess:
//                case BoundKind.Sequence:
//                case BoundKind.HoistedFieldAccess:
//                case BoundKind.OutVariablePendingInference:
//                case BoundKind.DeconstructionVariablePendingInference:
//                case BoundKind.OutDeconstructVarPendingInference:
//                case BoundKind.PseudoVariable:

                #endregion
            }
        }

        private static bool CheckValEscapeOfObjectInitializer(BoundObjectInitializerExpression initExpr, uint escapeFrom, uint escapeTo, DiagnosticBag diagnostics)
        {
            foreach (var expression in initExpr.Initializers)
            {
                if (expression.Kind == BoundKind.AssignmentOperator)
                {
                    var assignment = (BoundAssignmentOperator)expression;
                    if (!CheckValEscape(expression.Syntax, assignment.Right, escapeFrom, escapeTo, checkingReceiver: false, diagnostics: diagnostics))
                    {
                        return false;
                    }

                    var left = (BoundObjectInitializerMember)assignment.Left;
                    if (!CheckValEscape(left.Arguments, escapeFrom, escapeTo, diagnostics: diagnostics))
                    {
                        return false;
                    }
                }
                else
                {
                    if (!CheckValEscape(expression.Syntax, expression, escapeFrom, escapeTo, checkingReceiver: false, diagnostics: diagnostics))
                    {
                        return false;
                    }
                }
            }

            return true;
        }

        private static bool CheckValEscape(ImmutableArray<BoundExpression> expressions, uint escapeFrom, uint escapeTo, DiagnosticBag diagnostics)
        {
            foreach (var expression in expressions)
            {
                if (!CheckValEscape(expression.Syntax, expression, escapeFrom, escapeTo, checkingReceiver: false, diagnostics: diagnostics))
                {
                    return false;
                }
            }

            return true;
        }
    }
}
