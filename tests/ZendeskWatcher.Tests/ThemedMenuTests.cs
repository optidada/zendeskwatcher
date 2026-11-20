using ZendeskWatcher.UI;

namespace ZendeskWatcher.Tests;

/// <summary>Drop-down lists (ChoiceBox) wider than their texts: the hover highlight spans the whole row.</summary>
public class ThemedMenuTests
{
    [Fact]
    public void Rows_fill_a_list_wider_than_its_texts()
    {
        var (menuWidth, rowRights, displayRight) = OnUiThread(() =>
        {
            using var menu = new ContextMenuStrip { ShowImageMargin = false, ShowCheckMargin = true };
            foreach (var text in new[] { "Top left", "Top center", "Top right" })
                menu.Items.Add(new ToolStripMenuItem(text));
            ThemedMenu.Apply(menu, UiPalette.For(NotificationTheme.Optimizely));
            ThemedMenu.StretchTo(menu, 300);
            menu.Show(-3000, -3000);
            Application.DoEvents();
            var result = (menu.Width, menu.Items.Cast<ToolStripItem>().Select(i => i.Bounds.Right).ToList(), menu.DisplayRectangle.Right);
            menu.Close();
            return result;
        });

        Assert.Equal(300, menuWidth); // as wide as asked, not wider
        Assert.All(rowRights, right => Assert.InRange(right, displayRight - 3, displayRight));
    }

    private static T OnUiThread<T>(Func<T> action)
    {
        T result = default!;
        Exception? error = null;
        var thread = new Thread(() =>
        {
            try { result = action(); }
            catch (Exception ex) { error = ex; }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();
        if (error is not null)
            throw new InvalidOperationException("Failed on the UI thread", error);
        return result;
    }
}
