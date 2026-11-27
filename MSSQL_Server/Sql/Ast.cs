namespace MSSQL_Server.Sql
{
    public enum DataType : byte { Int = 1, BigInt = 2, Bit = 3, Float = 4, NVarChar = 5 }

    internal sealed record ObjectName(string? Database, string? Schema, string Name);

    internal abstract record Expr;
    internal sealed record Lit(object? Value) : Expr;
    internal sealed record Col(string? Table, string Name) : Expr;
    internal sealed record Var(string Name) : Expr;
    internal sealed record Bin(string Op, Expr L, Expr R) : Expr;
    internal sealed record Un(string Op, Expr E) : Expr;
    internal sealed record IsNullExpr(Expr E, bool Not) : Expr;
    internal sealed record LikeExpr(Expr E, Expr Pattern, bool Not) : Expr;
    internal sealed record InExpr(Expr E, List<Expr> List, bool Not) : Expr;
    internal sealed record BetweenExpr(Expr E, Expr Lo, Expr Hi, bool Not) : Expr;
    internal sealed record Func(string Name, List<Expr> Args, bool Star) : Expr;
    /// <summary>CASE [input] WHEN v THEN e ... [ELSE e] END (searched case: Input == null).</summary>
    internal sealed record CaseExpr(Expr? Input, List<(Expr When, Expr Then)> Whens, Expr? Else) : Expr;
    /// <summary>EXISTS (SELECT ...) - evaluated by running the sub-select.</summary>
    internal sealed record ExistsExpr(Select Select) : Expr;

    internal abstract record Stmt;
    internal sealed record NoOp : Stmt;
    internal sealed record BeginTran : Stmt;
    internal sealed record CommitTran : Stmt;
    internal sealed record RollbackTran : Stmt;
    internal sealed record CreateDb(string Name) : Stmt;
    internal sealed record DropDb(string Name) : Stmt;
    internal sealed record UseDb(string Name) : Stmt;
    internal sealed record ColumnDef(string Name, DataType Type, int Length, bool NotNull, bool PrimaryKey, bool Identity);
    internal sealed record CreateTable(ObjectName Name, List<ColumnDef> Columns) : Stmt;
    internal sealed record DropTable(ObjectName Name) : Stmt;
    internal sealed record Insert(ObjectName Table, List<string>? Columns, List<List<Expr>> Rows) : Stmt;
    /// <summary>One select-list item. AssignVar != null marks "@var = expr" assignment items.</summary>
    internal sealed record SelectItem(Expr? Expression, string? Alias, bool Star, string? StarTable, string? AssignVar = null);
    internal sealed record TableRef(ObjectName Name, string Alias);
    internal sealed record JoinClause(TableRef Table, Expr On, bool Left);
    internal sealed record OrderItem(Expr Expression, bool Descending);
    internal sealed record Select(
        int? Top, bool Distinct, List<SelectItem> Items, TableRef? From, List<JoinClause> Joins,
        Expr? Where, List<Expr> GroupBy, Expr? Having, List<OrderItem> OrderBy) : Stmt;
    internal sealed record Update(ObjectName Table, List<(string Column, Expr Value)> Sets, Expr? Where) : Stmt;
    internal sealed record Delete(ObjectName Table, Expr? Where) : Stmt;

    // ---- T-SQL scripting statements (needed by SSMS post-connect scripts) ----

    /// <summary>DECLARE @name AS type [= expr] [, ...] - scalar variables only.</summary>
    internal sealed record VarDecl(string Name, DataType Type, int Length, Expr? Init);
    internal sealed record DeclareVars(List<VarDecl> Vars) : Stmt;
    /// <summary>SET @name = expr.</summary>
    internal sealed record SetVar(string Name, Expr Value) : Stmt;
    /// <summary>BEGIN <statements> END (control-flow block; BEGIN TRAN is a separate node).</summary>
    internal sealed record BlockStmt(List<Stmt> Statements) : Stmt;
    /// <summary>IF cond <stmt> [ELSE <stmt>] - branches may be blocks or single statements.</summary>
    internal sealed record IfStmt(Expr Condition, Stmt Then, Stmt? Else) : Stmt;
    /// <summary>EXEC sp_executesql N'batch' [, N'@p type...' [, @p = value [OUTPUT] ...]] or EXEC ('batch').</summary>
    internal sealed record ExecParam(string Name, Expr Value, bool Output);
    internal sealed record ExecSql(Expr Batch, List<ExecParam>? Params) : Stmt;
    /// <summary>RETURN - stops executing the rest of the batch.</summary>
    internal sealed record ReturnStmt : Stmt;
}
