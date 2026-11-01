namespace Matgate.Agent.Setup;

internal static class SetupPreview
{
    public static void Save(string pathWithoutExtension)
    {
        Draw(new SetupForm(), pathWithoutExtension + "-setup.png");
        Draw(new ResultForm(new Installer.Result("Abc123-example-token-xyz", "B6A1A74FF49B59E48728364E704707897F3C88F02CCAA3EAD9237DFE224BA135", 7488, true)), pathWithoutExtension + "-result.png");
    }

    private static void Draw(Form form, string path)
    {
        using (form)
        {
            form.ShowInTaskbar = false;
            form.StartPosition = FormStartPosition.Manual;
            form.Location = new Point(-20000, -20000);
            form.Show();
            Application.DoEvents();
            using var bitmap = new Bitmap(form.Width, form.Height);
            form.DrawToBitmap(bitmap, new Rectangle(0, 0, form.Width, form.Height));
            bitmap.Save(path);
            form.Close();
        }
    }
}
