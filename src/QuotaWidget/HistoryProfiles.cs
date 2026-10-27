using System;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using QuotaWidget.Core;

namespace QuotaWidget.App;

public partial class MainWindow
{
    string? _historyCodexProfile, _historyProfilesCurrent;
    bool _refreshingProfiles;

    void RefreshHistoryProfiles()
    {
        _refreshingProfiles=true;
        try
        {
            _historyProfilesCurrent=_codex.ProfileKey;
            var profiles=_codex.SourceId is {} source ? _codex.Store.Profiles(source,DateTimeOffset.Now) : [];
            if(!profiles.Any(p=>p.ProfileKey==_historyCodexProfile))_historyCodexProfile=null;
            HistoryCodexProfile.Items.Clear();
            foreach(var p in profiles)
            {
                var current=p.ProfileKey==_codex.ProfileKey;
                var dates=$"{p.Bounds.First.ToLocalTime():M/d}–{p.Bounds.Last.ToLocalTime():M/d}";
                var item=new ComboBoxItem {Tag=p.ProfileKey,Content=(p.PlanLabel??"Codex")+" · "+(current?Loc.T("当前档位"):dates),
                    ToolTip=$"{p.Bounds.First.ToLocalTime():yyyy/M/d HH:mm} – {p.Bounds.Last.ToLocalTime():yyyy/M/d HH:mm}\n{p.ProfileKey}"};
                HistoryCodexProfile.Items.Add(item);
                if(p.ProfileKey==(_historyCodexProfile??_codex.ProfileKey))HistoryCodexProfile.SelectedItem=item;
            }
            var visible=profiles.Count>1?Visibility.Visible:Visibility.Collapsed;
            HistoryCodexProfile.Visibility=HistoryProfileNote.Visibility=visible;
            var selected=profiles.FirstOrDefault(p=>p.ProfileKey==(_historyCodexProfile??_codex.ProfileKey));
            HistoryProfileNote.Text=selected is null?"":$"{selected.Bounds.First.ToLocalTime():M/d}–{selected.Bounds.Last.ToLocalTime():M/d} · "+Loc.T("各档位分别统计");
        }
        finally{_refreshingProfiles=false;}
    }

    void HistoryCodexProfile_SelectionChanged(object sender,SelectionChangedEventArgs e)
    {
        if(_refreshingProfiles || HistoryCodexProfile.SelectedItem is not ComboBoxItem {Tag:string profile})return;
        _historyCodexProfile=profile==_codex.ProfileKey?null:profile;
        _codex.ReleaseCalendar();
        RenderHistory(force:true);
    }
}
