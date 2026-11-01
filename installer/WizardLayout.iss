// Copied from Prober's setup (Prober.Installer\WizardLayout.iss) so Orrery's wizard looks the same: the art as a
// side stripe on every page, the version under it, and page reflow. Only the version label's color differs.

function DesignTop(Ctrl: TControl): Integer;
begin
  Result := Ctrl.Top;
  if (Ctrl.Tag <> 0) and (Ctrl.Top = (Ctrl.Tag mod 65536) - 1) then
    Result := Ctrl.Tag div 65536;
end;

function OverlapsHorizontally(A, B: TControl): Boolean;
begin
  Result := (A.Left < B.Left + B.Width) and (B.Left < A.Left + A.Width);
end;

procedure ReflowSurface(Surface: TWinControl);
var
  Count, I, J, K, Placed, Gap: Integer;
  Order, Design, NewTop: array of Integer;
  Ctrl, Above: TControl;
begin
  if Surface = nil then Exit;
  Count := Surface.ControlCount;
  SetArrayLength(Order, Count);
  SetArrayLength(Design, Count);
  SetArrayLength(NewTop, Count);
  for I := 0 to Count - 1 do begin
    Design[I] := DesignTop(Surface.Controls[I]);
    J := I;
    while (J > 0) and (Design[Order[J - 1]] > Design[I]) do begin
      Order[J] := Order[J - 1];
      J := J - 1;
    end;
    Order[J] := I;
  end;
  Gap := ScaleY(3);
  for K := 0 to Count - 1 do begin
    I := Order[K];
    Ctrl := Surface.Controls[I];
    Placed := Design[I];
    if Ctrl.Visible then
      for J := 0 to K - 1 do begin
        Above := Surface.Controls[Order[J]];
        if Above.Visible and (Design[Order[J]] + ScaleY(8) <= Design[I]) and OverlapsHorizontally(Above, Ctrl) then begin
          if Design[I] + NewTop[Order[J]] - Design[Order[J]] > Placed then
            Placed := Design[I] + NewTop[Order[J]] - Design[Order[J]];
          if (Above is TNewStaticText) and (NewTop[Order[J]] + Above.Height + Gap > Placed) and
             (Design[Order[J]] + Above.Height > Design[I]) then
            Placed := NewTop[Order[J]] + Above.Height + Gap;
        end;
      end;
    NewTop[I] := Placed;
    Ctrl.Top := Placed;
    Ctrl.Tag := Design[I] * 65536 + Placed + 1;
  end;
end;

procedure ReflowActivePage;
begin
  if WizardForm.OuterNotebook.ActivePage = WizardForm.InnerPage then
    ReflowSurface(WizardForm.InnerNotebook.ActivePage)
  else
    ReflowSurface(WizardForm.OuterNotebook.ActivePage);
end;

procedure AddVersionToArt(Image: TBitmapImage);
var
  Lbl: TNewStaticText;
begin
  Lbl := TNewStaticText.Create(WizardForm);
  Lbl.Parent := Image.Parent;
  Lbl.Caption := 'v{#AppVersion}';
  Lbl.Font.Name := 'Consolas';
  Lbl.Left := Image.Left + (Image.Width - Lbl.Width) div 2;
  Lbl.Top := Image.Top + Image.Height - ScaleY(28);
  Lbl.StyleElements := [seClient, seBorder];
  Lbl.Font.Color := $00FFE0C8; // Light blue, to suit the art.
  Lbl.BringToFront;
end;

procedure SetUpSideStripe;
var
  Stripe: TBitmapImage;
  StripeWidth: Integer;
begin
  StripeWidth := WizardForm.WizardBitmapImage.Width;
  WizardForm.ClientWidth := WizardForm.ClientWidth + StripeWidth;
  WizardForm.Left := WizardForm.Left - StripeWidth div 2;
  Stripe := TBitmapImage.Create(WizardForm);
  Stripe.Parent := WizardForm.InnerPage;
  Stripe.Stretch := True;
  Stripe.SetBounds(0, 0, StripeWidth, WizardForm.InnerPage.ClientHeight);
  Stripe.Anchors := [akLeft, akTop, akBottom];
  if WizardForm.WizardBitmapImage.PngImage <> nil then
    Stripe.PngImage := WizardForm.WizardBitmapImage.PngImage
  else
    Stripe.Bitmap := WizardForm.WizardBitmapImage.Bitmap;
  WizardForm.WizardSmallBitmapImage.Visible := False;
  WizardForm.MainPanel.SetBounds(StripeWidth, WizardForm.MainPanel.Top,
    WizardForm.InnerPage.ClientWidth - StripeWidth, WizardForm.MainPanel.Height);
  WizardForm.Bevel1.SetBounds(StripeWidth, WizardForm.Bevel1.Top,
    WizardForm.InnerPage.ClientWidth - StripeWidth, WizardForm.Bevel1.Height);
  WizardForm.PageNameLabel.Width := WizardForm.MainPanel.ClientWidth - 2 * WizardForm.PageNameLabel.Left;
  WizardForm.PageDescriptionLabel.Width := WizardForm.MainPanel.ClientWidth - WizardForm.PageDescriptionLabel.Left -
    WizardForm.PageNameLabel.Left;
  WizardForm.InnerNotebook.SetBounds(WizardForm.InnerNotebook.Left + StripeWidth, WizardForm.InnerNotebook.Top,
    WizardForm.InnerNotebook.Width - StripeWidth, WizardForm.InnerNotebook.Height);
  AddVersionToArt(Stripe);
end;

procedure SetUpWizardArt;
begin
  SetUpSideStripe;
  AddVersionToArt(WizardForm.WizardBitmapImage);
  AddVersionToArt(WizardForm.WizardBitmapImage2);
end;
