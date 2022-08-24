﻿// Copyright 2012 Fan Shi
// 
// This file is part of the VBF project.
// 
// Licensed under the Apache License, Version 2.0 (the "License");
// you may not use this file except in compliance with the License.
// You may obtain a copy of the License at
// 
//     http://www.apache.org/licenses/LICENSE-2.0
// 
// Unless required by applicable law or agreed to in writing, software
// distributed under the License is distributed on an "AS IS" BASIS,
// WITHOUT WARRANTIES OR CONDITIONS OF ANY KIND, either express or implied.
// See the License for the specific language governing permissions and
// limitations under the License.

using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using VBF.Compilers;
using VBF.MiniSharp.Ast;
using Type = VBF.MiniSharp.Ast.Type;

namespace VBF.MiniSharp
{
    public class MemberDeclResolver : AstVisitor
    {
        internal const int c_SE_TypeNameMissing = 302;
        private const int c_SE_StaticBaseType = 303;
        private const int c_SE_CyclicBaseType = 304;
        private const int c_SE_FieldDuplicates = 310;
        private const int c_SE_MethodDuplicates = 311;
        private const int c_SE_ParameterDuplicates = 312;
        private CompilationErrorList m_errorList;
        private CompilationErrorManager m_errorManager;
        private TypeCollection m_types;

        public MemberDeclResolver(CompilationErrorManager errorManager, TypeCollection types)
        {
            m_errorManager = errorManager;
            m_types = types;
        }

        public CompilationErrorList ErrorList
        {
            get { return m_errorList; }
            set { m_errorList = value; }
        }

        public void DefineErrors()
        {
            m_errorManager.DefineError(c_SE_TypeNameMissing, 0, CompilationStage.SemanticAnalysis,
                "The type '{0}' could not be found.");

            m_errorManager.DefineError(c_SE_StaticBaseType, 0, CompilationStage.SemanticAnalysis,
                "The type '{0}' is a static class and it can't be used as a base class.");

            m_errorManager.DefineError(c_SE_CyclicBaseType, 0, CompilationStage.SemanticAnalysis,
                "The type '{0}' cannot be use as the base class because it is the same or one of the parent type of '{1}'.");

            m_errorManager.DefineError(c_SE_FieldDuplicates, 0, CompilationStage.SemanticAnalysis,
                "The type '{0}' has already defined a field named '{1}'.");

            m_errorManager.DefineError(c_SE_MethodDuplicates, 0, CompilationStage.SemanticAnalysis,
                "The type '{0}' has already defined a method named '{1}' with same parameter types.");

            m_errorManager.DefineError(c_SE_ParameterDuplicates, 0, CompilationStage.SemanticAnalysis,
                "The method '{0}' has already defined a parameter named '{1}'.");
        }

        private void AddError(int errorId, SourceSpan errorPosition, params object[] args)
        {
            if (m_errorList != null)
            {
                m_errorList.AddError(errorId, errorPosition, args);
            }
        }

        private TypeBase ResolveTypeRef(TypeRef typeRef)
        {
            TypeBase resolvedType = PrimaryType.Unknown;
            var name = typeRef.TypeName;

            if (!m_types.Contains(name.Content))
            {
                AddError(c_SE_TypeNameMissing, name.Span, name.Content);
            }
            else
            {
                typeRef.Type = m_types[name.Content];
                resolvedType = typeRef.Type;
            }

            return resolvedType;
        }

        private TypeBase ResolveTypeNode(Type typeNode)
        {
            Visit(typeNode);
            return typeNode.ResolvedType;
        }

        public override AstNode VisitProgram(Program ast)
        {
            Visit(ast.MainClass);

            foreach (var cd in ast.Classes)
            {
                Visit(cd);
            }

            foreach (var cd in ast.Classes)
            {
                //check cyclic inheritance

                //detect cyclic
                var currentBase = cd.BaseClass.Type;

                while (currentBase != null)
                {
                    if (currentBase == cd.Type)
                    {
                        AddError(c_SE_CyclicBaseType, cd.BaseClass.TypeName.Span, cd.BaseClass.TypeName.Content, cd.Name.Content);
                        break;
                    }

                    currentBase = (currentBase as CodeClassType).BaseType;
                }
            }

            return ast;
        }

        public override AstNode VisitMainClass(MainClass ast)
        {
            var mainMethod = new Method() { Name = "Main", IsStatic = true };
            mainMethod.ReturnType = PrimaryType.Void;

            var codeType = ast.Type as CodeClassType;

            codeType.StaticMethods.Add(mainMethod);

            return ast;
        }

        public override AstNode VisitClassDecl(ClassDecl ast)
        {
            if (ast.BaseClass.TypeName != null)
            {
                //resolve base class
                var baseTypeName = ast.BaseClass.TypeName.Content;

                if (!m_types.Contains(baseTypeName))
                {
                    AddError(c_SE_TypeNameMissing, ast.BaseClass.TypeName.Span, baseTypeName);

                    //leave ast.BaseClass.Type empty
                }
                else
                {
                    var type = m_types[baseTypeName] as CodeClassType;

                    Debug.Assert(type != null);

                    if (type.IsStatic)
                    {
                        AddError(c_SE_StaticBaseType, ast.BaseClass.TypeName.Span, baseTypeName);
                    }

                    ast.BaseClass.Type = type;
                    (ast.Type as CodeClassType).BaseType = type;
                }
            }

            //resolve member decl types

            //fields
            foreach (var field in ast.Fields)
            {
                field.FieldInfo = new Field() { DeclaringType = ast.Type };
                Visit(field);
            }

            //methods
            foreach (var method in ast.Methods)
            {
                method.MethodInfo = new Method() { DeclaringType = ast.Type };
                Visit(method);
            }

            return ast;
        }

        public override AstNode VisitFieldDecl(FieldDecl ast)
        {
            var declType = ast.FieldInfo.DeclaringType as CodeClassType;
            var fieldName = ast.FieldName;
            //check name conflict
            if (declType.Fields.Contains(fieldName.Content))
            {
                AddError(c_SE_FieldDuplicates, fieldName.Span, declType.Name, fieldName.Content);
            }

            ast.FieldInfo.Name = fieldName.Content;

            var typeNode = ast.Type;
            //check type
            TypeBase resolvedType = ResolveTypeNode(typeNode);

            ast.FieldInfo.Type = resolvedType;
            declType.Fields.Add(ast.FieldInfo);

            return ast;
        }

        public override AstNode VisitMethodDecl(MethodDecl ast)
        {
            var method = ast.MethodInfo;

            method.Name = ast.Name.Content;
            method.IsStatic = false;

            //step 1, resolve return type
            var returnTypeNode = ast.ReturnType;
            var returnType = ResolveTypeNode(returnTypeNode);

            method.ReturnType = returnType;

            //step 2, resolve parameter types
            bool allValid = true;
            HashSet<string> paramNames = new HashSet<string>();

            int paramIndex = 1; //0 is "this"
            foreach (var parameter in ast.Parameters)
            {
                var paramTypeNode = parameter.Type;
                var paramType = ResolveTypeNode(paramTypeNode);

                if (paramType == null)
                {
                    allValid = false;
                }

                var paramInfo = new Parameter() { Name = parameter.ParameterName.Content, Type = paramType, Index = paramIndex };

                if (paramNames.Contains(paramInfo.Name))
                {
                    AddError(c_SE_ParameterDuplicates, parameter.ParameterName.Span, method.Name, paramInfo.Name);
                    allValid = false;
                }
                else
                {
                    paramNames.Add(paramInfo.Name);
                    method.Parameters.Add(paramInfo);
                }

                paramIndex++;
            }

            //step 3, check overloading

            if (returnType == null || !allValid)
            {
                //resolve type failed
                return ast;
            }

            var declType = method.DeclaringType as CodeClassType;

            var methodsSameName = declType.Methods.Where(m => m.Name == method.Name).ToArray();
            foreach (var overloadMethod in methodsSameName)
            {
                if (overloadMethod.Parameters.Count == method.Parameters.Count)
                {
                    bool allTypeSame = true;
                    for (int i = 0; i < overloadMethod.Parameters.Count; i++)
                    {
                        if (overloadMethod.Parameters[i].Type != method.Parameters[i].Type)
                        {
                            allTypeSame = false;
                            break;
                        }
                    }

                    if (allTypeSame)
                    {
                        AddError(c_SE_MethodDuplicates, ast.Name.Span, method.DeclaringType.Name, method.Name);
                    }
                }
            }

            declType.Methods.Add(method);
            return ast;
        }

        public override AstNode VisitIdentifierType(IdentifierType ast)
        {
            ast.ResolvedType = ResolveTypeRef(ast.Type);
            return ast;
        }

        public override AstNode VisitIntArrayType(IntArrayType ast)
        {
            ast.ResolvedType = ArrayType.IntArray;
            return ast;
        }

        public override AstNode VisitIntegerType(IntegerType ast)
        {
            ast.ResolvedType = PrimaryType.Int;
            return ast;
        }

        public override AstNode VisitBooleanType(BooleanType ast)
        {
            ast.ResolvedType = PrimaryType.Boolean;
            return ast;
        }
    }
}
