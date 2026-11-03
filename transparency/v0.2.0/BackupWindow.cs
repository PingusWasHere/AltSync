using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using AltSync.Core;

namespace AltSync;
internal sealed class BackupWindow : Window
{
    private readonly PasswordBox password=new(){Padding=new Thickness(10),MaxLength=1024};
    private readonly PasswordBox confirm=new(){Padding=new Thickness(10),MaxLength=1024};
    private readonly CheckBox accounts=new(){Content="Accounts",IsChecked=true,Margin=new Thickness(0,12,0,4)};
    private readonly CheckBox library=new(){Content="Profiles, games and window positions",IsChecked=true,Margin=new Thickness(0,4,0,4)};
    private readonly CheckBox settings=new(){Content="Preferences",IsChecked=false,Margin=new Thickness(0,4,0,12)};
    private readonly TextBlock status=new(){TextWrapping=TextWrapping.Wrap,Foreground=Brushes.Gray,FontSize=12,Margin=new Thickness(0,12,0,12)};
    private readonly Button submit=new(){Padding=new Thickness(16,8,16,8)};
    public byte[]? Exported {get;private set;}
    public BackupPayload? Imported {get;private set;}
    private BackupPayload? preview;
    public BackupWindow(Window owner,Vault vault,Library savedLibrary,IReadOnlySet<long> selected,byte[]? input=null)
    {
        Owner=owner;Icon=owner.Icon;Title=input is null?"AltSync backup":"Import AltSync backup";Width=460;SizeToContent=SizeToContent.Height;ResizeMode=ResizeMode.NoResize;WindowStartupLocation=WindowStartupLocation.CenterOwner;Background=new SolidColorBrush(Color.FromRgb(25,25,25));Foreground=Brushes.WhiteSmoke;
        var panel=new StackPanel{Margin=new Thickness(24)};panel.Children.Add(new TextBlock{Text=input is null?"Create an encrypted backup":"Open your encrypted backup",FontSize=19,Margin=new Thickness(0,0,0,14)});
        panel.Children.Add(new TextBlock{Text=input is null?selected.Count>0?"Selected accounts will be included. Keep the password somewhere safe.":"All saved accounts will be included. Keep the password somewhere safe.":"Enter the backup password. You can review its contents before importing.",TextWrapping=TextWrapping.Wrap,FontSize=12,Foreground=Brushes.LightGray,Margin=new Thickness(0,0,0,16)});
        panel.Children.Add(new TextBlock{Text="Password",Margin=new Thickness(0,0,0,7)});panel.Children.Add(password);
        if(input is null){panel.Children.Add(new TextBlock{Text="Confirm password",Margin=new Thickness(0,12,0,7)});panel.Children.Add(confirm);panel.Children.Add(accounts);panel.Children.Add(library);panel.Children.Add(settings);}
        panel.Children.Add(status);var actions=new StackPanel{Orientation=Orientation.Horizontal,HorizontalAlignment=HorizontalAlignment.Right};var cancel=new Button{Content="Cancel",Padding=new Thickness(16,8,16,8),Margin=new Thickness(0,0,8,0)};submit.Content=input is null?"Save backup":"Unlock backup";
        foreach(var button in new[]{cancel,submit}){button.Background=new SolidColorBrush(Color.FromRgb(42,42,42));button.Foreground=Brushes.WhiteSmoke;button.BorderBrush=new SolidColorBrush(Color.FromRgb(65,65,65));}
        foreach(var box in new[]{password,confirm}){box.Background=new SolidColorBrush(Color.FromRgb(20,20,20));box.Foreground=Brushes.WhiteSmoke;box.BorderBrush=new SolidColorBrush(Color.FromRgb(65,65,65));}
        cancel.Click+=(_,_)=>Close();actions.Children.Add(cancel);actions.Children.Add(submit);panel.Children.Add(actions);Content=panel;
        submit.Click+=async(_,_)=>
        {
            if(preview is not null){Imported=preview;DialogResult=true;return;}
            if(input is null&&(password.Password.Length<12||password.Password!=confirm.Password)){status.Text="Use at least 12 characters and enter the same password twice.";return;}
            submit.IsEnabled=false;password.IsEnabled=false;confirm.IsEnabled=false;
            try
            {
                string secret=password.Password;status.Text=input is null?"Encrypting backup…":"Unlocking backup…";
                if(input is null)
                {
                    bool includeAccounts=accounts.IsChecked==true,includeLibrary=library.IsChecked==true,includeSettings=settings.IsChecked==true;
                    Exported=await Task.Run(()=>PortableBackup.Export(vault,savedLibrary,secret,selected,includeAccounts,includeLibrary,includeSettings));DialogResult=true;
                }
                else
                {preview=await Task.Run(()=>PortableBackup.Read(input,secret));password.Clear();confirm.Clear();status.Text=$"Ready to merge {preview.Accounts.Count} accounts, {preview.Library?.Profiles.Count??0} profiles and {preview.Library?.Games.Count??0} saved games. {(preview.Settings is not null?"Preferences will be restored.":"Current preferences will be kept.")}";submit.Content="Import backup";}
            }
            catch(Exception ex){status.Text=ex is InvalidDataException or ArgumentException?ex.Message:"The backup could not be processed.";}
            finally{submit.IsEnabled=true;password.IsEnabled=preview is null;confirm.IsEnabled=true;}
        };
        Loaded+=(_,_)=>password.Focus();Closed+=(_,_)=>{password.Clear();confirm.Clear();};
    }
}
