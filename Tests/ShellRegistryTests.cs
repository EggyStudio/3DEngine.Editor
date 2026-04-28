using Editor.Shell;
using FluentAssertions;
using Xunit;
namespace Engine.Tests.Editor;
[Trait("Category", "Unit")]
public class ShellRegistryTests
{
    // -- Defaults --
    [Fact]
    public void Version_Starts_At_Zero()
    {
        new ShellRegistry().Version.Should().Be(0);
    }
    [Fact]
    public void Current_Returns_Empty_Descriptor_By_Default()
    {
        var registry = new ShellRegistry();
        registry.Current.Should().NotBeNull();
        registry.Current.Panels.Should().BeEmpty();
    }
    // -- RegisterSource --
    [Fact]
    public void RegisterSource_Bumps_Version()
    {
        var registry = new ShellRegistry();
        registry.RegisterSource("a", new ShellSource());
        registry.Version.Should().Be(1);
        registry.RegisterSource("a", new ShellSource()); // upsert still bumps
        registry.Version.Should().Be(2);
    }
    [Fact]
    public void RegisterSource_Surfaces_Builder_Output_In_Current()
    {
        var registry = new ShellRegistry();
        var src = new ShellSource
        {
            Builders = new IEditorShellBuilder[] { new TestBuilder("p1", "Panel 1", DockZone.Left) }
        };
        registry.RegisterSource(ShellSourceIds.Static, src);
        registry.Current.Panels.Should().HaveCount(1);
        registry.Current.Panels[0].Id.Should().Be("p1");
        registry.Current.Panels[0].Title.Should().Be("Panel 1");
    }
    [Fact]
    public void RegisterSource_Null_SourceId_Throws()
    {
        var registry = new ShellRegistry();
        var act = () => registry.RegisterSource("", new ShellSource());
        act.Should().Throw<ArgumentException>();
    }
    [Fact]
    public void RegisterSource_Null_Source_Throws()
    {
        var registry = new ShellRegistry();
        var act = () => registry.RegisterSource("x", null!);
        act.Should().Throw<ArgumentNullException>();
    }
    // -- RemoveSource --
    [Fact]
    public void RemoveSource_Drops_Contribution_And_Bumps_Version()
    {
        var registry = new ShellRegistry();
        registry.RegisterSource("dyn", new ShellSource
        {
            Builders = new IEditorShellBuilder[] { new TestBuilder("a", "A", DockZone.Center) }
        });
        registry.Current.Panels.Should().HaveCount(1);
        registry.RemoveSource("dyn");
        registry.Current.Panels.Should().BeEmpty();
        registry.Version.Should().Be(2);
    }
    [Fact]
    public void RemoveSource_Unknown_Id_Is_NoOp()
    {
        var registry = new ShellRegistry();
        registry.RemoveSource("does-not-exist");
        registry.Version.Should().Be(0);
    }
    // -- Multi-source merging + collision policy --
    [Fact]
    public void Dynamic_Source_Overrides_Static_On_Same_Panel_Id()
    {
        var registry = new ShellRegistry();
        registry.RegisterSource(ShellSourceIds.Static, new ShellSource
        {
            Precedence = 0,
            Builders = new IEditorShellBuilder[] { new TestBuilder("inspector", "Static Inspector", DockZone.Right) }
        });
        registry.RegisterSource(ShellSourceIds.Dynamic, new ShellSource
        {
            Precedence = 100,
            Builders = new IEditorShellBuilder[] { new TestBuilder("inspector", "Hot-Reloaded Inspector", DockZone.Right) }
        });
        registry.Current.Panels.Should().HaveCount(1);
        registry.Current.Panels[0].Title.Should().Be("Hot-Reloaded Inspector");
    }
    [Fact]
    public void Different_Panel_Ids_From_Multiple_Sources_All_Appear()
    {
        var registry = new ShellRegistry();
        registry.RegisterSource(ShellSourceIds.Static, new ShellSource
        {
            Builders = new IEditorShellBuilder[] { new TestBuilder("a", "A", DockZone.Left) }
        });
        registry.RegisterSource(ShellSourceIds.Dynamic, new ShellSource
        {
            Precedence = 100,
            Builders = new IEditorShellBuilder[] { new TestBuilder("b", "B", DockZone.Right) }
        });
        registry.Current.Panels.Select(p => p.Id).Should().BeEquivalentTo(new[] { "a", "b" });
    }
    [Fact]
    public void CustomCss_From_All_Sources_Concatenates()
    {
        var registry = new ShellRegistry();
        registry.RegisterSource(ShellSourceIds.Static, new ShellSource { CustomCss = new[] { "/* static */" } });
        registry.RegisterSource(ShellSourceIds.Dynamic, new ShellSource { Precedence = 100, CustomCss = new[] { "/* dynamic */" } });
        registry.Current.CustomCss.Should().BeEquivalentTo("/* static */", "/* dynamic */");
    }
    // -- Events / threading --
    [Fact]
    public void Changed_Event_Fires_On_RegisterSource()
    {
        var registry = new ShellRegistry();
        int eventCount = 0;
        registry.Changed += () => eventCount++;
        registry.RegisterSource("x", new ShellSource());
        eventCount.Should().Be(1);
    }
    [Fact]
    public void Changed_Event_Handler_Can_Read_Current_Without_Deadlock()
    {
        var registry = new ShellRegistry();
        ShellDescriptor? captured = null;
        registry.Changed += () => captured = registry.Current;
        registry.RegisterSource("x", new ShellSource
        {
            Builders = new IEditorShellBuilder[] { new TestBuilder("p", "P", DockZone.Center) }
        });
        captured.Should().NotBeNull();
        captured!.Panels.Should().HaveCount(1);
    }
    [Fact]
    public void Concurrent_RegisterSource_Does_Not_Corrupt_State()
    {
        var registry = new ShellRegistry();
        const int iterations = 100;
        Parallel.For(0, iterations, i =>
        {
            registry.RegisterSource($"src-{i % 4}", new ShellSource
            {
                Builders = new IEditorShellBuilder[] { new TestBuilder($"p-{i}", $"P{i}", DockZone.Center) }
            });
        });
        registry.Version.Should().Be(iterations);
        registry.Current.Should().NotBeNull();
    }
    // -- PanelDescriptor / ShellDescriptor defaults (unchanged) --
    [Fact]
    public void PanelDescriptor_Has_Sensible_Defaults()
    {
        var panel = new PanelDescriptor();
        panel.Id.Should().BeEmpty();
        panel.Title.Should().BeEmpty();
        panel.DefaultZone.Should().Be(DockZone.Left);
        panel.InitialSize.Should().BeApproximately(0.25f, 0.001f);
        panel.Closeable.Should().BeTrue();
        panel.Visible.Should().BeTrue();
        panel.Content.Should().BeNull();
        panel.Icon.Should().BeNull();
        panel.TabGroupId.Should().BeNull();
        panel.Route.Should().BeNull();
    }
    [Fact]
    public void ShellDescriptor_Defaults_Are_Empty()
    {
        var desc = new ShellDescriptor();
        desc.Panels.Should().BeEmpty();
        desc.Metadata.Should().BeEmpty();
    }
    // -- Test fixtures --
    private sealed class TestBuilder : IEditorShellBuilder
    {
        private readonly string _id, _title;
        private readonly DockZone _zone;
        public TestBuilder(string id, string title, DockZone zone) { _id = id; _title = title; _zone = zone; }
        public int Order => 0;
        public void Build(IShellBuilder shell) => shell.Panel(_id, _title, _zone, _ => { });
    }
}
