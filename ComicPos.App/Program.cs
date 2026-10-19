using ComicPos.Data;

namespace ComicPos.App;

static class Program
{
    [STAThread]
    static void Main()
    {
        ApplicationConfiguration.Initialize();

        using var db = new ComicDbContext();
        var inventory = new InventoryService(db);
        var checkout = new CheckoutService(db, inventory);

        Application.Run(new MainForm(inventory, checkout));
    }
}