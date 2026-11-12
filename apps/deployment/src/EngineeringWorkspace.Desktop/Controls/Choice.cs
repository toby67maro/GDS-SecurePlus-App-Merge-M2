namespace EngineeringWorkspace.Desktop.Controls;

/// <summary>A choice in a drop-down: what is stored and what is shown.</summary>
public sealed record Choice<T>(T Value, string Label)
{
    public override string ToString() => Label;
}
