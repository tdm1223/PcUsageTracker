namespace PcUsageTracker.App;

internal static class UiText
{
    public static bool Korean { get; set; }
    public static string T(string english, string korean) => Korean ? korean : english;

    static readonly Dictionary<string, string> KoreanText = new(StringComparer.Ordinal)
    {
        ["PcUsageTracker — Report"] = "PcUsageTracker — 사용 보고서",
        ["Dashboard"] = "대시보드", ["Today + Week + Month"] = "오늘 · 이번 주 · 이번 달",
        ["All-time"] = "전체 기간", ["Compare"] = "기간 비교", ["Today"] = "오늘",
        ["This week"] = "이번 주", ["This month"] = "이번 달", ["Applications"] = "앱 목록",
        ["Applications && categories..."] = "앱 및 분류...", ["Applications & categories"] = "앱 및 분류 관리",
        ["Categories"] = "분류", ["Tracked"] = "기록 시간", ["Active"] = "활성 시간",
        ["Idle"] = "자리 비움", ["Top application"] = "가장 많이 사용한 앱",
        ["Daily usage"] = "일별 사용량", ["Full day"] = "하루 전체", ["7 days"] = "7일", ["30 days"] = "30일",
        ["Process"] = "프로세스", ["Category"] = "분류", ["Duration"] = "사용 시간", ["Share"] = "비율",
        ["Measure"] = "항목", ["Current"] = "현재 기간", ["Previous"] = "이전 기간", ["Change"] = "차이",
        ["Today vs yesterday · same elapsed time"] = "오늘과 어제 · 동일 경과 시간 기준",
        ["This week vs last week · same elapsed time"] = "이번 주와 지난주 · 동일 경과 시간 기준",
        ["Search apps or category"] = "앱 또는 분류 검색", ["Time ↓"] = "시간 많은 순", ["Time ↑"] = "시간 적은 순",
        ["Name A–Z"] = "이름 오름차순", ["Name Z–A"] = "이름 내림차순",
        ["Edit application..."] = "앱 설정 수정...", ["Delete history && stop tracking"] = "기록 삭제 및 추적 제외",
        ["Open report"] = "사용 보고서 열기", ["Export to Excel..."] = "엑셀로 내보내기...",
        ["Import from Excel..."] = "엑셀에서 가져오기...", ["Back up all data..."] = "전체 데이터 백업...",
        ["Restore full backup..."] = "전체 백업 복원...", ["Idle threshold..."] = "자리 비움 기준...",
        ["Autostart on Windows login"] = "Windows 로그인 시 자동 시작", ["Check for updates..."] = "업데이트 확인...",
        ["Checking for updates..."] = "업데이트 확인 중...", ["Downloading update..."] = "업데이트 다운로드 중...",
        ["Open data folder"] = "데이터 폴더 열기", ["Quit"] = "종료",
        ["Select an application above."] = "위에서 앱을 선택하세요.", ["Override"] = "직접 지정",
        ["Save changes"] = "변경 사항 저장", ["Clear customizations"] = "사용자 설정 초기화",
        ["Edit..."] = "수정...", ["Delete"] = "삭제", ["Close"] = "닫기", ["Add..."] = "추가...",
        ["Process identity"] = "프로세스 이름", ["Display name"] = "표시 이름", ["Color"] = "색상",
        ["Executable path"] = "실행 파일 경로", ["Alias"] = "별칭", ["Name"] = "이름", ["Sort order"] = "정렬 순서",
        ["Category choices save immediately. Ctrl/Shift selects multiple rows and applies to all."] = "분류는 즉시 저장됩니다. Ctrl/Shift로 여러 앱을 선택해 함께 적용할 수 있습니다.",
        ["Add category"] = "분류 추가", ["Edit category"] = "분류 수정",
        ["Other is the permanent fallback, so its name cannot be changed."] = "기본 분류 Other의 이름은 변경할 수 없습니다.",
        ["Save"] = "저장", ["Cancel"] = "취소", ["OK"] = "확인", ["Idle threshold"] = "자리 비움 기준",
        ["Stop tracking after this many minutes of no input:"] = "입력이 없을 때 자리 비움으로 전환할 시간:",
        ["minutes (1–60)"] = "분 (1–60)",
        ["Click the timeline to inspect a time."] = "타임라인을 클릭하면 해당 시각의 기록을 볼 수 있습니다.",
        ["No activity recorded"] = "기록된 활동이 없습니다", ["No matching applications"] = "검색 결과가 없습니다",
        ["No activity in this range"] = "이 기간의 기록이 없습니다", ["Loading timeline..."] = "타임라인 불러오는 중...",
        ["Coding"] = "코딩", ["Game"] = "게임", ["Communication"] = "커뮤니케이션", ["Browsing"] = "웹 탐색",
        ["System"] = "시스템", ["Other"] = "기타", ["Other / unassigned"] = "기타 / 미지정",
        ["Could not delete the category."] = "분류를 삭제하지 못했습니다.",
        ["Could not save the category."] = "분류를 저장하지 못했습니다.",
        ["Could not save the application rule."] = "앱 설정을 저장하지 못했습니다.",
        ["Could not clear the application rule."] = "앱 설정을 초기화하지 못했습니다.",
        ["Could not update the selected application categories."] = "선택한 앱의 분류를 변경하지 못했습니다.",
        ["A category with that name already exists, or the entered values are invalid."] = "같은 이름의 분류가 이미 있거나 입력값이 올바르지 않습니다.",
    };

    public static string Translate(string text) => Korean && KoreanText.TryGetValue(text, out var value) ? value : text;
    public static string Category(string? text) => text is null ? "" : Translate(text);

    public static void Apply(Control control)
    {
        if (!Korean) return;
        if (control is Form or Label or ButtonBase or TabPage or GroupBox) control.Text = Translate(control.Text);
        if (control is TextBox input) input.PlaceholderText = Translate(input.PlaceholderText);
        if (control is DataGridView grid)
        {
            foreach (DataGridViewColumn column in grid.Columns) column.HeaderText = Translate(column.HeaderText);
            if (grid.ContextMenuStrip is { } context) ApplyMenu(context.Items);
        }
        if (control is ComboBox combo)
        {
            var selected = combo.SelectedIndex;
            for (var index = 0; index < combo.Items.Count; index++)
                if (combo.Items[index] is string item) combo.Items[index] = Translate(item);
            if (selected >= 0 && selected < combo.Items.Count && combo.SelectedIndex != selected) combo.SelectedIndex = selected;
        }
        foreach (Control child in control.Controls) Apply(child);
    }

    public static void ApplyMenu(ToolStripItemCollection items)
    {
        if (!Korean) return;
        foreach (ToolStripItem item in items)
        {
            if (item.Text is { } text) item.Text = Translate(text);
            if (item is ToolStripDropDownItem dropdown) ApplyMenu(dropdown.DropDownItems);
        }
    }
}
