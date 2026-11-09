// Brings the wizard to the front once it's on screen, and readies the license text before the first page shows.
//
// Setup asks for administrator rights, and the elevated copy Windows starts after the UAC prompt often opens
// behind the window it was started from: Windows' foreground lock keeps it from taking the foreground by itself.
// Making the window topmost and back again puts it on top whatever the lock says; taking the foreground (borrowing
// the current foreground thread's input for a moment) gives it the keyboard as well when Windows allows that.
//
// The license page's rich edit control is otherwise created when that page first shows, and the first time on a
// machine that load can take a while, showing an unpainted white block between Welcome and License. Creating it
// while the wizard is still hidden moves that wait to before the window appears.

const
  SWP_NOSIZE = $0001;
  SWP_NOMOVE = $0002;
  SWP_SHOWWINDOW = $0040;
  HWND_TOPMOST = -1;
  HWND_NOTOPMOST = -2;

function SetWindowPos(hWnd: HWND; hWndInsertAfter: NativeInt; X, Y, cx, cy: Integer; uFlags: UINT): BOOL;
external 'SetWindowPos@user32.dll stdcall';

function SetForegroundWindow(hWnd: HWND): BOOL;
external 'SetForegroundWindow@user32.dll stdcall';

function GetForegroundWindow: HWND;
external 'GetForegroundWindow@user32.dll stdcall';

function IsWindowVisible(hWnd: HWND): BOOL;
external 'IsWindowVisible@user32.dll stdcall';

function GetWindowThreadProcessId(hWnd: HWND; lpdwProcessId: NativeInt): DWORD;
external 'GetWindowThreadProcessId@user32.dll stdcall';

function GetCurrentThreadId: DWORD;
external 'GetCurrentThreadId@kernel32.dll stdcall';

function AttachThreadInput(idAttach, idAttachTo: DWORD; fAttach: Boolean): Boolean;
external 'AttachThreadInput@user32.dll stdcall';

function SetTimer(hWnd: HWND; nIDEvent: UINT_PTR; uElapse: UINT; lpTimerFunc: NativeInt): UINT_PTR;
external 'SetTimer@user32.dll stdcall';

function KillTimer(hWnd: HWND; nIDEvent: UINT_PTR): BOOL;
external 'KillTimer@user32.dll stdcall';

var
  FrontTimer: UINT_PTR;
  FrontTries: Integer;

procedure StopFrontTimer;
begin
  if FrontTimer <> 0 then begin
    KillTimer(0, FrontTimer);
    FrontTimer := 0;
  end;
end;

procedure BringWizardToFront(Wnd: HWND);
var
  Foreground: HWND;
  Mine, Theirs: DWORD;
begin
  SetWindowPos(Wnd, HWND_TOPMOST, 0, 0, 0, 0, SWP_NOMOVE or SWP_NOSIZE or SWP_SHOWWINDOW);
  SetWindowPos(Wnd, HWND_NOTOPMOST, 0, 0, 0, 0, SWP_NOMOVE or SWP_NOSIZE or SWP_SHOWWINDOW);
  Foreground := GetForegroundWindow;
  if Foreground = Wnd then Exit;
  Mine := GetCurrentThreadId;
  Theirs := 0;
  if Foreground <> 0 then
    Theirs := GetWindowThreadProcessId(Foreground, 0);
  if (Theirs <> 0) and (Theirs <> Mine) and AttachThreadInput(Mine, Theirs, True) then begin
    SetForegroundWindow(Wnd);
    AttachThreadInput(Mine, Theirs, False);
  end else
    SetForegroundWindow(Wnd);
end;

// Runs every 50 ms until the wizard is on screen (giving up after about five seconds), then once more.
procedure FrontTimerProc(Arg1: HWND; Arg2: UINT; Arg3: UINT_PTR; Arg4: DWORD);
begin
  FrontTries := FrontTries + 1;
  if (WizardForm = nil) or (FrontTries > 100) then begin
    StopFrontTimer;
    Exit;
  end;
  if IsWindowVisible(WizardForm.Handle) then begin
    StopFrontTimer;
    BringWizardToFront(WizardForm.Handle);
  end;
end;

procedure PrepareWizardFront;
var
  Ignored: HWND;
begin
  Ignored := WizardForm.LicenseMemo.Handle;
  if not WizardSilent then
    FrontTimer := SetTimer(0, 0, 50, CreateCallback(@FrontTimerProc));
end;
