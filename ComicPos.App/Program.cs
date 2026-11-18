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
        var history = new SalesHistoryService(db);
        var customers = new CustomerService(db);
        var pullLists = new PullListService(db);
        var import = new ImportService(db);
        var focReport = new FocReportService(db);
        var email = new EmailService();
        var emailSettings = new EmailSettingsService();

        Application.Run(new MainForm(inventory, checkout, history, customers, pullLists, import, focReport, email, emailSettings));
    }
}
