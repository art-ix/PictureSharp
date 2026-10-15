namespace PictureSharp.Tests.Infrastructure;

// Test case lists for [MethodDataSource]: TUnit takes IEnumerable<T> or IEnumerable<(T1, T2, ...)>, and these keep
// the collection-initializer form ({ a, b }) and collection expressions of the data members.

/// <summary>Test cases with one argument.</summary>
public sealed class TestCases<T1> : List<T1>
{
    public TestCases()
    {
    }

    public TestCases(IEnumerable<T1> items)
        : base(items)
    {
    }
}

/// <summary>Test cases with 2 arguments.</summary>
public sealed class TestCases<T1, T2> : List<(T1, T2)>
{
    public void Add(T1 arg1, T2 arg2) => Add((arg1, arg2));
}

/// <summary>Test cases with 3 arguments.</summary>
public sealed class TestCases<T1, T2, T3> : List<(T1, T2, T3)>
{
    public void Add(T1 arg1, T2 arg2, T3 arg3) => Add((arg1, arg2, arg3));
}

/// <summary>Test cases with 4 arguments.</summary>
public sealed class TestCases<T1, T2, T3, T4> : List<(T1, T2, T3, T4)>
{
    public void Add(T1 arg1, T2 arg2, T3 arg3, T4 arg4) => Add((arg1, arg2, arg3, arg4));
}

/// <summary>Test cases with 5 arguments.</summary>
public sealed class TestCases<T1, T2, T3, T4, T5> : List<(T1, T2, T3, T4, T5)>
{
    public void Add(T1 arg1, T2 arg2, T3 arg3, T4 arg4, T5 arg5) => Add((arg1, arg2, arg3, arg4, arg5));
}

/// <summary>Test cases with 8 arguments.</summary>
public sealed class TestCases<T1, T2, T3, T4, T5, T6, T7, T8> : List<(T1, T2, T3, T4, T5, T6, T7, T8)>
{
    public void Add(T1 arg1, T2 arg2, T3 arg3, T4 arg4, T5 arg5, T6 arg6, T7 arg7, T8 arg8) => Add((arg1, arg2, arg3, arg4, arg5, arg6, arg7, arg8));
}
