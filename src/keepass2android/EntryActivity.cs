/*
This file is part of Keepass2Android, Copyright 2013 Philipp Crocoll. This file is based on Keepassdroid, Copyright Brian Pellin.

  Keepass2Android is free software: you can redistribute it and/or modify
  it under the terms of the GNU General Public License as published by
  the Free Software Foundation, either version 3 of the License, or
  (at your option) any later version.

  Keepass2Android is distributed in the hope that it will be useful,
  but WITHOUT ANY WARRANTY; without even the implied warranty of
  MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE.  See the
  GNU General Public License for more details.

  You should have received a copy of the GNU General Public License
  along with Keepass2Android.  If not, see <http://www.gnu.org/licenses/>.
  */

using System;
using System.Collections.Generic;
using System.Text;
using System.Linq;
using System.Threading;
using Android.App;
using Android.Content;
using Android.OS;
using Android.Runtime;
using Android.Text;
using Android.Views;
using Android.Widget;
using Android.Preferences;
using Android.Text.Method;
using System.Globalization;
using System.Net;
using Android.Content.PM;
using Android.Webkit;
using Android.Graphics;
using Java.IO;
using keepass2android.EntryActivityClasses;
using KeePassLib;
using KeePassLib.Security;
using KeePassLib.Utility;
using Keepass2android.Pluginsdk;
using keepass2android.Io;
using KeePass.Util.Spr;
using Uri = Android.Net.Uri;

namespace keepass2android
{

	[Activity (Label = "@string/app_name", ConfigurationChanges=ConfigChanges.Orientation|ConfigChanges.KeyboardHidden,
        Theme = "@style/MyTheme_ActionBar")]
	public class EntryActivity : LockCloseActivity 
	{
		public const String KeyEntry = "entry";
		public const String KeyRefreshPos = "refresh_pos";
		public const String KeyCloseAfterCreate = "close_after_create";
		public const String KeyGroupFullPath = "groupfullpath_key";

		public static void Launch(Activity act, PwEntry pw, int pos, AppTask appTask, ActivityFlags? flags = null)
		{
			Intent i = new Intent(act, typeof(EntryActivity));

			i.PutExtra(KeyEntry, pw.Uuid.ToHexString());
			i.PutExtra(KeyRefreshPos, pos);

			if (flags != null)
				i.SetFlags((ActivityFlags) flags);

			appTask.ToIntent(i);
			if (flags != null && (((ActivityFlags) flags) | ActivityFlags.ForwardResult) == ActivityFlags.ForwardResult)
				act.StartActivity(i);
			else
				act.StartActivityForResult(i, 0);
		}

		public EntryActivity (IntPtr javaReference, JniHandleOwnership transfer)
			: base(javaReference, transfer)
		{
			
		}

		public EntryActivity()
		{
			_activityDesign = new ActivityDesign(this);
		}

		protected PwEntry Entry;

		private static Typeface _passwordFont;

		internal bool _showPassword;
		private int _pos;

		AppTask _appTask;

	    struct ProtectedTextviewGroup
	    {
	        public TextView ProtectedField;
	        public TextView VisibleProtectedField;
        }

		private List<ProtectedTextviewGroup> _protectedTextViews;
		private IMenu _menu;

		private readonly Dictionary<string, List<IPopupMenuItem>> _popupMenuItems =
			new Dictionary<string, List<IPopupMenuItem>>();

		private readonly Dictionary<string, IStringView> _stringViews = new Dictionary<string, IStringView>();
		private readonly List<PluginMenuOption> _pendingMenuOptions = new List<PluginMenuOption>();
		
		//make sure _timer doesn't go out of scope:
		private Timer _timer;
		private PluginActionReceiver _pluginActionReceiver;
		private PluginFieldReceiver _pluginFieldReceiver;
		private ActivityDesign _activityDesign;


		protected void SetEntryView()
		{
			SetContentView(Resource.Layout.entry_view);
		}

		protected void SetupEditButtons() {
			View edit =  FindViewById(Resource.Id.entry_edit);
			if (App.Kp2a.GetDb().CanWrite)
			{
				edit.Visibility = ViewStates.Visible;
				edit.Click += (sender, e) =>
				{
					EntryEditActivity.Launch(this, Entry, _appTask);
				};	
			}
			else
			{
				edit.Visibility = ViewStates.Gone;
			}
			
		}

		
		private class PluginActionReceiver : BroadcastReceiver
		{
			private readonly EntryActivity _activity;

			public PluginActionReceiver(EntryActivity activity)
			{
				_activity = activity;
			}

			public override void OnReceive(Context context, Intent intent)
			{
				var pluginPackage = intent.GetStringExtra(Strings.ExtraSender);
				if (new PluginDatabase(context).IsValidAccessToken(pluginPackage,
				                                                   intent.GetStringExtra(Strings.ExtraAccessToken),
				                                                   Strings.ScopeCurrentEntry))
				{
					if (intent.GetStringExtra(Strings.ExtraEntryId) != _activity.Entry.Uuid.ToHexString())
					{
						Kp2aLog.Log("received action for wrong entry " + intent.GetStringExtra(Strings.ExtraEntryId));
						return;
					}
					_activity.AddPluginAction(pluginPackage,
					                          intent.GetStringExtra(Strings.ExtraFieldId),
											  intent.GetStringExtra(Strings.ExtraActionId),
					                          intent.GetStringExtra(Strings.ExtraActionDisplayText),
					                          intent.GetIntExtra(Strings.ExtraActionIconResId, -1),
					                          intent.GetBundleExtra(Strings.ExtraActionData));
				}
				else
				{
					Kp2aLog.Log("received invalid request. Plugin not authorized.");
				}
			}
		}

		private class PluginFieldReceiver : BroadcastReceiver
		{
			private readonly EntryActivity _activity;

			public PluginFieldReceiver(EntryActivity activity)
			{
				_activity = activity;
			}

			public override void OnReceive(Context context, Intent intent)
			{
				if (intent.GetStringExtra(Strings.ExtraEntryId) != _activity.Entry.Uuid.ToHexString())
				{
					Kp2aLog.Log("received field for wrong entry " + intent.GetStringExtra(Strings.ExtraEntryId));
					return;
				}
				if (!new PluginDatabase(context).IsValidAccessToken(intent.GetStringExtra(Strings.ExtraSender),
				                                                    intent.GetStringExtra(Strings.ExtraAccessToken),
				                                                    Strings.ScopeCurrentEntry))
				{
					Kp2aLog.Log("received field with invalid access token from " + intent.GetStringExtra(Strings.ExtraSender));
					return;
				}
				string key = intent.GetStringExtra(Strings.ExtraFieldId);
				string value = intent.GetStringExtra(Strings.ExtraFieldValue);
				bool isProtected = intent.GetBooleanExtra(Strings.ExtraFieldProtected, false);
				_activity.SetPluginField(key, value, isProtected);
			}
		}

		private void SetPluginField(string key, string value, bool isProtected)
		{
			//update or add the string view:
			IStringView existingField;
			if (_stringViews.TryGetValue(key, out existingField))
			{
				existingField.Text = value;
			}
			else
			{
				ViewGroup extraGroup = (ViewGroup) FindViewById(Resource.Id.extra_strings);
				var view = CreateExtraSection(key, value, isProtected);
				extraGroup.AddView(view.View);
			}

			//update the Entry output in the App database and notify the CopyToClipboard service

		    if (App.Kp2a.GetDb()?.LastOpenedEntry != null)
		    {
		        App.Kp2a.GetDb().LastOpenedEntry.OutputStrings.Set(key, new ProtectedString(isProtected, value));
		        Intent updateKeyboardIntent = new Intent(this, typeof(CopyToClipboardService));
		        updateKeyboardIntent.SetAction(Intents.UpdateKeyboard);
		        updateKeyboardIntent.PutExtra(KeyEntry, Entry.Uuid.ToHexString());
		        StartService(updateKeyboardIntent);

		        //notify plugins
		        NotifyPluginsOnModification(Strings.PrefixString + key);
		    }
		}

		private void AddPluginAction(string pluginPackage, string fieldId, string popupItemId, string displayText, int iconId, Bundle bundleExtra)
		{
			if (fieldId != null)
			{
				try
				{
					if (!_popupMenuItems.ContainsKey(fieldId))
					{
						Kp2aLog.Log("Did not find field with key " + fieldId);
						return;
					}
					//create a new popup item for the plugin action:
					var newPopup = new PluginPopupMenuItem(this, pluginPackage, fieldId, popupItemId, displayText, iconId, bundleExtra);
					//see if we already have a popup item for this field with the same item id
					var popupsForField = _popupMenuItems[fieldId];
					var popupItemPos = popupsForField.FindIndex(0,
															item =>
															(item is PluginPopupMenuItem) &&
															((PluginPopupMenuItem)item).PopupItemId == popupItemId);

					//replace existing or add
					if (popupItemPos >= 0)
					{
						popupsForField[popupItemPos] = newPopup;
					}
					else
					{
						popupsForField.Add(newPopup);
					}
				}
				catch (Exception e)
				{
					Kp2aLog.LogUnexpectedError(e);
				}
				
			}
			else
			{
				//we need to add an option to the  menu.
				//As it is not sure that OnCreateOptionsMenu was called yet, we cannot access _menu without a check:

				Intent i = new Intent(Strings.ActionEntryActionSelected);
				i.SetPackage(pluginPackage);
				i.PutExtra(Strings.ExtraActionData, bundleExtra);
				i.PutExtra(Strings.ExtraSender, PackageName);
				PluginHost.AddEntryToIntent(i, App.Kp2a.GetDb().LastOpenedEntry);

				var menuOption = new PluginMenuOption()
					{
						DisplayText = displayText,
						Icon = PackageManager.GetResourcesForApplication(pluginPackage).GetDrawable(iconId),
						Intent = i
					};

				if (_menu != null)
				{
					AddMenuOption(menuOption);
				}
				else
				{
					lock (_pendingMenuOptions)
					{
						_pendingMenuOptions.Add(menuOption);
					}

				}


			}
		}

		private void AddMenuOption(PluginMenuOption menuOption)
		{
			var menuItem = _menu.Add(menuOption.DisplayText);
			menuItem.SetIcon(menuOption.Icon);
			menuItem.SetIntent(menuOption.Intent);
		}

		


		protected override void OnCreate(Bundle savedInstanceState)
		{

			ISharedPreferences prefs = PreferenceManager.GetDefaultSharedPreferences(this);

			long usageCount = prefs.GetLong(GetString(Resource.String.UsageCount_key), 0);

			ISharedPreferencesEditor edit = prefs.Edit();
			edit.PutLong(GetString(Resource.String.UsageCount_key), usageCount + 1);
			edit.Commit();

			_showPassword =
				!prefs.GetBoolean(GetString(Resource.String.maskpass_key), Resources.GetBoolean(Resource.Boolean.maskpass_default));
            
            RequestWindowFeature(WindowFeatures.IndeterminateProgress);
			
			_activityDesign.ApplyTheme(); 
			base.OnCreate(savedInstanceState);
			

			

			SetEntryView();

			Database db = App.Kp2a.GetDb();
			// Likely the app has been killed exit the activity 
			if (!db.Loaded || (App.Kp2a.QuickLocked))
			{
				Finish();
				return;
			}

			SetResult(KeePass.ExitNormal);

			Intent i = Intent;
			PwUuid uuid = new PwUuid(MemUtil.HexStringToByteArray(i.GetStringExtra(KeyEntry)));
			_pos = i.GetIntExtra(KeyRefreshPos, -1);

			_appTask = AppTask.GetTaskInOnCreate(savedInstanceState, Intent);

			Entry = db.Entries[uuid];
			
			// Refresh Menu contents in case onCreateMenuOptions was called before Entry was set
			ActivityCompat.InvalidateOptionsMenu(this);

			// Update last access time.
			Entry.Touch(false);

			if (PwDefs.IsTanEntry(Entry) && prefs.GetBoolean(GetString(Resource.String.TanExpiresOnUse_key), Resources.GetBoolean(Resource.Boolean.TanExpiresOnUse_default)) && ((Entry.Expires == false) || Entry.ExpiryTime > DateTime.Now))
			{
				PwEntry backupEntry = Entry.CloneDeep();
				Entry.ExpiryTime = DateTime.Now;
				Entry.Expires = true;
				Entry.Touch(true);
				RequiresRefresh();
				UpdateEntry update = new UpdateEntry(this, App.Kp2a, backupEntry, Entry, null);
				ProgressTask pt = new ProgressTask(App.Kp2a, this, update);
				pt.Run();
			}
			FillData();

			SetupEditButtons();
			
			App.Kp2a.GetDb().LastOpenedEntry = new PwEntryOutput(Entry, App.Kp2a.GetDb().KpDatabase);

			_pluginActionReceiver = new PluginActionReceiver(this);
			RegisterReceiver(_pluginActionReceiver, new IntentFilter(Strings.ActionAddEntryAction));
			_pluginFieldReceiver = new PluginFieldReceiver(this);
			RegisterReceiver(_pluginFieldReceiver, new IntentFilter(Strings.ActionSetEntryField));

			new Thread(NotifyPluginsOnOpen).Start();

			//the rest of the things to do depends on the current app task:
			_appTask.CompleteOnCreateEntryActivity(this);
		}

		private void NotifyPluginsOnOpen()
		{
			Intent i = new Intent(Strings.ActionOpenEntry);
			i.PutExtra(Strings.ExtraSender, PackageName);
			AddEntryToIntent(i);

			foreach (var plugin in new PluginDatabase(this).GetPluginsWithAcceptedScope(Strings.ScopeCurrentEntry))
			{
				i.SetPackage(plugin);
				SendBroadcast(i);
			}

			new Kp2aTotp().OnOpenEntry();

		}
		private void NotifyPluginsOnModification(string fieldId)
		{
			Intent i = new Intent(Strings.ActionEntryOutputModified);
			i.PutExtra(Strings.ExtraSender, PackageName);
			i.PutExtra(Strings.ExtraFieldId, fieldId);
			AddEntryToIntent(i);


			foreach (var plugin in new PluginDatabase(this).GetPluginsWithAcceptedScope(Strings.ScopeCurrentEntry))
			{
				i.SetPackage(plugin);
				SendBroadcast(i);
			}
		}

		

		internal void StartNotificationsService(bool closeAfterCreate)
		{
			Intent showNotIntent = new Intent(this, typeof (CopyToClipboardService));
			showNotIntent.SetAction(Intents.ShowNotification);
			showNotIntent.PutExtra(KeyEntry, Entry.Uuid.ToHexString());
			_appTask.PopulatePasswordAccessServiceIntent(showNotIntent);
			showNotIntent.PutExtra(KeyCloseAfterCreate, closeAfterCreate);

			StartService(showNotIntent);
		}


		private String getDateTime(DateTime dt)
		{
			return dt.ToLocalTime().ToString("g", CultureInfo.CurrentUICulture);
		}

		private String concatTags(List<string> tags)
		{
			StringBuilder sb = new StringBuilder();
			foreach (string tag in tags)
			{
				sb.Append(tag);
				sb.Append(", ");
			}
			if (tags.Count > 0)
				sb.Remove(sb.Length - 2, 2);
			return sb.ToString();
		}

		private void PopulateExtraStrings()
		{
			ViewGroup extraGroup = (ViewGroup) FindViewById(Resource.Id.extra_strings);
		    bool hasExtras = false;
			IEditMode editMode = new DefaultEdit();
			if (KpEntryTemplatedEdit.IsTemplated(App.Kp2a.GetDb(), this.Entry))
				editMode = new KpEntryTemplatedEdit(App.Kp2a.GetDb(), this.Entry);
			foreach (var key in  editMode.SortExtraFieldKeys(Entry.Strings.GetKeys().Where(key=> !PwDefs.IsStandardField(key))))
			{
				if (editMode.IsVisible(key))
				{
					hasExtras = true;
					var value = Entry.Strings.Get(key);
					var stringView = CreateExtraSection(key, value.ReadString(), value.IsProtected);
					extraGroup.AddView(stringView.View);
				}
			}
            FindViewById(Resource.Id.extra_strings_container).Visibility = hasExtras ? ViewStates.Visible : ViewStates.Gone;
		}

		private ExtraStringView CreateExtraSection(string key, string value, bool isProtected)
		{
			LinearLayout layout = new LinearLayout(this, null) {Orientation = Orientation.Vertical};
			LinearLayout.LayoutParams layoutParams = new LinearLayout.LayoutParams(ViewGroup.LayoutParams.FillParent,
			                                                                       ViewGroup.LayoutParams.WrapContent);

			layout.LayoutParameters = layoutParams;
			View viewInflated = LayoutInflater.Inflate(Resource.Layout.entry_extrastring_title, null);
			TextView keyView = viewInflated.FindViewById<TextView>(Resource.Id.entry_title);
			if (key != null)
				keyView.Text = key;

			layout.AddView(viewInflated);
			RelativeLayout valueViewContainer =
				(RelativeLayout) LayoutInflater.Inflate(Resource.Layout.entry_extrastring_value, null);
			var valueView = valueViewContainer.FindViewById<TextView>(Resource.Id.entry_extra);
		    var valueViewVisible = valueViewContainer.FindViewById<TextView>(Resource.Id.entry_extra_visible);
		    if (value != null)
		    {
		        valueView.Text = value;
                valueViewVisible.Text = value;

            }
		    SetPasswordTypeface(valueViewVisible);
		    if (isProtected)
		    {
		        RegisterProtectedTextView(valueView, valueViewVisible);
                
		    }
		    else
		    {
		        valueView.Visibility = ViewStates.Gone;
		    }

			layout.AddView(valueViewContainer);
			var stringView = new ExtraStringView(layout, valueView, valueViewVisible, keyView);

			_stringViews.Add(key, stringView);
			RegisterTextPopup(valueViewContainer, valueViewContainer.FindViewById(Resource.Id.extra_vdots), key, isProtected);

			return stringView;

		}



		private List<IPopupMenuItem> RegisterPopup(string popupKey, View clickView, View anchorView)
		{
			clickView.Click += (sender, args) =>
				{
					ShowPopup(anchorView, popupKey);
				};
			_popupMenuItems[popupKey] = new List<IPopupMenuItem>();
			return _popupMenuItems[popupKey];
		}
				internal Uri WriteBinaryToFile(string key, bool writeToCacheDirectory)
		{
			ProtectedBinary pb = Entry.Binaries.Get(key);
			System.Diagnostics.Debug.Assert(pb != null);
			if (pb == null)
				throw new ArgumentException();


			ISharedPreferences prefs = PreferenceManager.GetDefaultSharedPreferences(this);
			string binaryDirectory = prefs.GetString(GetString(Resource.String.BinaryDirectory_key), GetString(Resource.String.BinaryDirectory_default));
			if (writeToCacheDirectory)
				binaryDirectory = CacheDir.Path + File.Separator + AttachmentContentProvider.AttachmentCacheSubDir;

			string filepart = key;
		    if (writeToCacheDirectory)
		    {
		        Java.Lang.String javaFilename = new Java.Lang.String(filepart);
                filepart = javaFilename.ReplaceAll("[^a-zA-Z0-9.-]", "_");
            }
		    var targetFile = new File(binaryDirectory, filepart);

			File parent = targetFile.ParentFile;

			if (parent == null || (parent.Exists() && !parent.IsDirectory))
			{
				Toast.MakeText(this,
							   Resource.String.error_invalid_path,
							   ToastLength.Long).Show();
				return null;
			}

			if (!parent.Exists())
			{
				// Create parent directory
				if (!parent.Mkdirs())
				{
					Toast.MakeText(this,
								   Resource.String.error_could_not_create_parent,
								   ToastLength.Long).Show();
					return null;

				}
			}
			string filename = targetFile.AbsolutePath;
			Uri fileUri = Uri.FromFile(targetFile);

			byte[] pbData = pb.ReadData();
			try
			{
				System.IO.File.WriteAllBytes(filename, pbData);
			}
			catch (Exception exWrite)
			{
				Toast.MakeText(this, GetString(Resource.String.SaveAttachment_Failed, new Java.Lang.Object[] { filename })
					+ exWrite.Message, ToastLength.Long).Show();
				return null;
			}
			finally
			{
				MemUtil.ZeroByteArray(pbData);
			}
			Toast.MakeText(this, GetString(Resource.String.SaveAttachment_doneMessage, new Java.Lang.Object[] { filename }), ToastLength.Short).Show();
			if (writeToCacheDirectory)
			{
				return Uri.Parse("content://" + AttachmentContentProvider.Authority + "/"
											  + filename);
			}
			return fileUri;
		}

		internal void OpenBinaryFile(Android.Net.Uri uri)
		{


			String theMimeType = GetMimeType(uri.Path);
			if (theMimeType != null)
			{

				Intent theIntent = new Intent(Intent.ActionView);
				theIntent.AddFlags(ActivityFlags.NewTask | ActivityFlags.ExcludeFromRecents);
				theIntent.SetDataAndType(uri, theMimeType);
				try
				{
					StartActivity(theIntent);
				}
				catch (ActivityNotFoundException)
				{
					//ignore
					Toast.MakeText(this, "Couldn't open file", ToastLength.Short).Show();
				}
			}

		}



		private void RegisterProtectedTextView(TextView protectedTextView, TextView visibleTextView)
		{
		    var protectedTextviewGroup = new ProtectedTextviewGroup { ProtectedField = protectedTextView, VisibleProtectedField = visibleTextView};
		    _protectedTextViews.Add(protectedTextviewGroup);
            SetPasswordStyle(protectedTextviewGroup);
		}


		private void PopulateBinaries()
		{
			ViewGroup binariesGroup = (ViewGroup) FindViewById(Resource.Id.binaries);
			foreach (KeyValuePair<string, ProtectedBinary> pair in Entry.Binaries)
			{
				String key = pair.Key;


				RelativeLayout valueViewContainer =
					(RelativeLayout) LayoutInflater.Inflate(Resource.Layout.entry_extrastring_value, null);
				var valueView = valueViewContainer.FindViewById<TextView>(Resource.Id.entry_extra);
				if (key != null)
					valueView.Text = key;

				string popupKey = Strings.PrefixBinary + key;

				var itemList = RegisterPopup(popupKey, valueViewContainer, valueViewContainer.FindViewById(Resource.Id.extra_vdots));
				itemList.Add(new WriteBinaryToFilePopupItem(key, this));
				itemList.Add(new OpenBinaryPopupItem(key, this));
				itemList.Add(new ViewImagePopupItem(key, this));




				binariesGroup.AddView(valueViewContainer);
				/*
				Button binaryButton = new Button(this);
				RelativeLayout.LayoutParams layoutParams = new RelativeLayout.LayoutParams(ViewGroup.LayoutParams.FillParent, ViewGroup.LayoutParams.WrapContent);
				binaryButton.Text = key;
				binaryButton.SetCompoundDrawablesWithIntrinsicBounds( Resources.GetDrawable(Android.Resource.Drawable.IcMenuSave),null, null, null);
				binaryButton.Click += (sender, e) => 
				{
					Button btnSender = (Button)(sender);

					AlertDialog.Builder builder = new AlertDialog.Builder(this);
					builder.SetTitle(GetString(Resource.String.SaveAttachmentDialog_title));
					
					builder.SetMessage(GetString(Resource.String.SaveAttachmentDialog_text));
					
					builder.SetPositiveButton(GetString(Resource.String.SaveAttachmentDialog_save), (dlgSender, dlgEvt) => 
					                                                                                                                    {
							
						});
					
					builder.SetNegativeButton(GetString(Resource.String.SaveAttachmentDialog_open), (dlgSender, dlgEvt) => 
					                                                                                                                   {
							
						});

					Dialog dialog = builder.Create();
					dialog.Show();


				};
				binariesGroup.AddView(binaryButton,layoutParams);
				*/

			}
			FindViewById(Resource.Id.entry_binaries_label).Visibility = Entry.Binaries.Any() ? ViewStates.Visible : ViewStates.Gone;
		}

		// url = file path or whatever suitable URL you want.
		public static String GetMimeType(String url)
		{
			String type = null;
			String extension = MimeTypeMap.GetFileExtensionFromUrl(url);
			if (extension != null)
			{
				MimeTypeMap mime = MimeTypeMap.Singleton;
				type = mime.GetMimeTypeFromExtension(extension.ToLowerInvariant());
			}
			return type;
		}

		public override void OnBackPressed()
		{
			base.OnBackPressed();
			//OverridePendingTransition(Resource.Animation.anim_enter_back, Resource.Animation.anim_leave_back);
		}

		protected void FillData()
		{
			_protectedTextViews = new List<ProtectedTextviewGroup>();
			ImageView iv = (ImageView) FindViewById(Resource.Id.icon);
			if (iv != null)
			{
				iv.SetImageDrawable(Resources.GetDrawable(Resource.Drawable.ic00));
			}



            SupportActionBar.Title = Entry.Strings.ReadSafe(PwDefs.TitleField);
            SupportActionBar.SetDisplayHomeAsUpEnabled(true);
            SupportActionBar.SetHomeButtonEnabled(true);

			PopulateGroupText (Resource.Id.entry_group_name, Resource.Id.entryfield_group_container, KeyGroupFullPath);

			PopulateStandardText(Resource.Id.entry_user_name, Resource.Id.entryfield_container_username, PwDefs.UserNameField);
			PopulateStandardText(Resource.Id.entry_url, Resource.Id.entryfield_container_url, PwDefs.UrlField);
			PopulateStandardText(new List<int> { Resource.Id.entry_password, Resource.Id.entry_password_visible}, Resource.Id.entryfield_container_password, PwDefs.PasswordField);
		    
            RegisterProtectedTextView(FindViewById<TextView>(Resource.Id.entry_password), FindViewById<TextView>(Resource.Id.entry_password_visible));

			RegisterTextPopup(FindViewById<RelativeLayout> (Resource.Id.groupname_container),
				              FindViewById (Resource.Id.entry_group_name), KeyGroupFullPath);

			RegisterTextPopup(FindViewById<RelativeLayout>(Resource.Id.username_container),
			                  FindViewById(Resource.Id.username_vdots), PwDefs.UserNameField);

			RegisterTextPopup(FindViewById<RelativeLayout>(Resource.Id.url_container),
			                  FindViewById(Resource.Id.url_vdots), PwDefs.UrlField)
				.Add(new GotoUrlMenuItem(this));
			RegisterTextPopup(FindViewById<RelativeLayout>(Resource.Id.password_container),
			                  FindViewById(Resource.Id.password_vdots), PwDefs.PasswordField);


			PopulateText(Resource.Id.entry_created, Resource.Id.entryfield_container_created, getDateTime(Entry.CreationTime));
			PopulateText(Resource.Id.entry_modified, Resource.Id.entryfield_container_modified, getDateTime(Entry.LastModificationTime));

			if (Entry.Expires)
			{
				PopulateText(Resource.Id.entry_expires, Resource.Id.entryfield_container_expires, getDateTime(Entry.ExpiryTime));

			}
			else
			{
				PopulateText(Resource.Id.entry_expires, Resource.Id.entryfield_container_expires, null);
			}
			PopulateStandardText(Resource.Id.entry_comment, Resource.Id.entryfield_container_comment, PwDefs.NotesField);
			RegisterTextPopup(FindViewById<RelativeLayout>(Resource.Id.comment_container),
							  FindViewById(Resource.Id.comment_vdots), PwDefs.NotesField);

			PopulateText(Resource.Id.entry_tags, Resource.Id.entryfield_container_tags, concatTags(Entry.Tags));
			PopulateText(Resource.Id.entry_override_url, Resource.Id.entryfield_container_overrideurl, Entry.OverrideUrl);

			PopulateExtraStrings();

			PopulateBinaries();

			SetPasswordStyle();
		}

		

		protected override void OnDestroy()
		{
			NotifyPluginsOnClose();
			if (_pluginActionReceiver != null)
				UnregisterReceiver(_pluginActionReceiver);
			if (_pluginFieldReceiver != null)
				UnregisterReceiver(_pluginFieldReceiver);
			base.OnDestroy();
		}

		private void NotifyPluginsOnClose()
		{
			Intent i = new Intent(Strings.ActionCloseEntryView);
			i.PutExtra(Strings.ExtraSender, PackageName);
			foreach (var plugin in new PluginDatabase(this).GetPluginsWithAcceptedScope(Strings.ScopeCurrentEntry))
			{
				i.SetPackage(plugin);
				SendBroadcast(i);
			}
		}
		private List<IPopupMenuItem> RegisterTextPopup(View container, View anchor, string fieldKey)
		{
			return RegisterTextPopup(container, anchor, fieldKey, Entry.Strings.GetSafe(fieldKey).IsProtected);
		}

		private List<IPopupMenuItem> RegisterTextPopup(View container, View anchor, string fieldKey, bool isProtected)
		{
			string popupKey = Strings.PrefixString + fieldKey;
			var popupItems = RegisterPopup(
				popupKey,
				container,
				anchor);
			popupItems.Add(new CopyToClipboardPopupMenuIcon(this, _stringViews[fieldKey]));
			if (isProtected)
				popupItems.Add(new ToggleVisibilityPopupMenuItem(this));
			return popupItems;
		}



		private void ShowPopup(View anchor, string popupKey)
		{
			//PopupMenu popupMenu = new PopupMenu(this, FindViewById(Resource.Id.entry_user_name));
			PopupMenu popupMenu = new PopupMenu(this, anchor);

			AccessManager.PreparePopup(popupMenu);
			int itemId = 0;
			foreach (IPopupMenuItem popupItem in _popupMenuItems[popupKey])
			{
				popupMenu.Menu.Add(0, itemId, 0, popupItem.Text)
				         .SetIcon(popupItem.Icon);
				itemId++;
			}

			popupMenu.MenuItemClick += delegate(object sender, PopupMenu.MenuItemClickEventArgs args)
				{
					_popupMenuItems[popupKey][args.Item.ItemId].HandleClick();
				};
			popupMenu.Show();
		}

		
		private void SetPasswordTypeface(TextView textView)
		{
			if (_passwordFont == null)
			{
				_passwordFont = Typeface.CreateFromAsset(Assets, "SourceCodePro-Regular.ttf");
			}
			textView.Typeface = _passwordFont;	
		}

	    private void PopulateText(int viewId, int containerViewId, String text)
	    {
	        PopulateText(new List<int> {viewId}, containerViewId, text);
	    }


        private void PopulateText(List<int> viewIds, int containerViewId, String text)
		{
			View container = FindViewById(containerViewId);
		    foreach (int viewId in viewIds)
		    {
		        TextView tv = (TextView) FindViewById(viewId);
		        if (String.IsNullOrEmpty(text))
		        {
		            container.Visibility = tv.Visibility = ViewStates.Gone;
		        }
		        else
		        {
		            container.Visibility = tv.Visibility = ViewStates.Visible;
		            tv.Text = text;

		        }
		    }
		}

	    private void PopulateStandardText(int viewId, int containerViewId, String key)
	    {
	        PopulateStandardText(new List<int> {viewId}, containerViewId, key);
	    }


        private void PopulateStandardText(List<int> viewIds, int containerViewId, String key)
		{
			String value = Entry.Strings.ReadSafe(key);
			value = SprEngine.Compile(value, new SprContext(Entry, App.Kp2a.GetDb().KpDatabase, SprCompileFlags.All));
			PopulateText(viewIds, containerViewId, value);
			_stringViews.Add(key, new StandardStringView(viewIds, containerViewId, this));
		}

		private void PopulateGroupText(int viewId, int containerViewId, String key)
		{
			string groupName = null;
			if (PreferenceManager.GetDefaultSharedPreferences(this).GetBoolean(
				"ShowGroupInEntry", false))
			{
				groupName = Entry.ParentGroup.GetFullPath();
			}
			PopulateText(viewId, containerViewId, groupName);
			_stringViews.Add (key, new StandardStringView (new List<int>{viewId}, containerViewId, this));
		}

		private void RequiresRefresh()
		{
			Intent ret = new Intent();
			ret.PutExtra(KeyRefreshPos, _pos);
			_appTask.ToIntent(ret);
			SetResult(KeePass.ExitRefresh, ret);
		}

		protected override void OnActivityResult(int requestCode, Result resultCode, Intent data) {
			base.OnActivityResult(requestCode, resultCode, data);
			if (AppTask.TryGetFromActivityResult(data, ref _appTask))
			{
				//make sure app task is passed to calling activity.
				//the result code might be modified later.
				Intent retData = new Intent();
				_appTask.ToIntent(retData);
				SetResult(KeePass.ExitNormal, retData);	
			}

		
			

			if ( resultCode == KeePass.ExitRefresh || resultCode == KeePass.ExitRefreshTitle ) {
				if ( resultCode == KeePass.ExitRefreshTitle ) {
					RequiresRefresh ();
				}
				Recreate();
			}
		}

		public override bool OnCreateOptionsMenu(IMenu menu)
		{
			_menu = menu;
			base.OnCreateOptionsMenu(menu);

			MenuInflater inflater = MenuInflater;
			inflater.Inflate(Resource.Menu.entry, menu);

			lock (_pendingMenuOptions)
			{
				foreach (var option in _pendingMenuOptions)
					AddMenuOption(option);
				_pendingMenuOptions.Clear();
			}


			UpdateTogglePasswordMenu();

			return true;
		}

		public override bool OnPrepareOptionsMenu(IMenu menu)
		{
			Util.PrepareDonateOptionMenu(menu, this);
			return base.OnPrepareOptionsMenu(menu);
		}

        

		

		private void UpdateTogglePasswordMenu()
		{
			IMenuItem togglePassword = _menu.FindItem(Resource.Id.menu_toggle_pass);
			if (_showPassword)
			{
				togglePassword.SetTitle(Resource.String.menu_hide_password);
			}
			else
			{
				togglePassword.SetTitle(Resource.String.show_password);
			}
		}

		private void SetPasswordStyle()
		{
			foreach (ProtectedTextviewGroup group in _protectedTextViews)
            {
                SetPasswordStyle(group);
            }
        }

        private void SetPasswordStyle(ProtectedTextviewGroup group)
        {
            group.VisibleProtectedField.Visibility = _showPassword ? ViewStates.Visible : ViewStates.Gone;
            group.ProtectedField.Visibility = !_showPassword ? ViewStates.Visible : ViewStates.Gone;

            SetPasswordTypeface(group.VisibleProtectedField);

            group.ProtectedField.InputType = InputTypes.ClassText | InputTypes.TextVariationPassword;
        }

        protected override void OnResume()
		{
			ClearCache();
			base.OnResume();
			_activityDesign.ReapplyTheme();
		}

		public void ClearCache()
		{
			try
			{
				File dir = new File(CacheDir.Path + File.Separator + AttachmentContentProvider.AttachmentCacheSubDir);
				if (dir.IsDirectory)
				{
					IoUtil.DeleteDir(dir);
				}
			}
			catch (Exception)
			{

			}
		}

		public override bool OnOptionsItemSelected(IMenuItem item)
		{
			//check if this is a plugin action
			if ((item.Intent != null) && (item.Intent.Action == Strings.ActionEntryActionSelected))
			{
				//yes. let the plugin handle the click:
				SendBroadcast(item.Intent);
				return true;
			}

			switch (item.ItemId)
			{
				case Resource.Id.menu_donate:
					return Util.GotoDonateUrl(this);
                case Resource.Id.menu_delete:
                    DeleteEntry task = new DeleteEntry(this, App.Kp2a, Entry,
                        new ActionOnFinish(this, (success, message, activity) => { if (success) { RequiresRefresh(); Finish();}}));
                    task.Start();
                    break;
                case Resource.Id.menu_toggle_pass:
					if (_showPassword)
					{
						item.SetTitle(Resource.String.show_password);
						_showPassword = false;
					}
					else
					{
						item.SetTitle(Resource.String.menu_hide_password);
						_showPassword = true;
					}
					SetPasswordStyle();

					return true;

				case Resource.Id.menu_lock:
					App.Kp2a.LockDatabase();
					return true;
				case Android.Resource.Id.Home:
					//Currently the action bar only displays the home button when we come from a previous activity.
					//So we can simply Finish. See this page for information on how to do this in more general (future?) cases:
					//http://developer.android.com/training/implementing-navigation/ancestral.html
					Finish();
					//OverridePendingTransition(Resource.Animation.anim_enter_back, Resource.Animation.anim_leave_back);

					return true;
			}


			return base.OnOptionsItemSelected(item);
		}

		
		
		internal void AddUrlToEntry(string url, Action<EntryActivity> finishAction)
		{
			PwEntry initialEntry = Entry.CloneDeep();

			PwEntry newEntry = Entry;
			newEntry.History = newEntry.History.CloneDeep();
			newEntry.CreateBackup(null);

			newEntry.Touch(true, false); // Touch *after* backup

			//if there is no URL in the entry, set that field. If it's already in use, use an additional (not existing) field
			if (String.IsNullOrEmpty(newEntry.Strings.ReadSafe(PwDefs.UrlField)))
			{
				newEntry.Strings.Set(PwDefs.UrlField, new ProtectedString(false, url));
			}
			else
			{
				int c = 1;
				while (newEntry.Strings.Get("KP2A_URL_" + c) != null)
				{
					c++;
				}

				newEntry.Strings.Set("KP2A_URL_" + c, new ProtectedString(false, url));
			}

			//save the entry:

			ActionOnFinish closeOrShowError = new ActionOnFinish(this, (success, message, activity) =>
			{
				OnFinish.DisplayMessage(this, message);
			    finishAction((EntryActivity)activity);
			});


			RunnableOnFinish runnable = new UpdateEntry(this, App.Kp2a, initialEntry, newEntry, closeOrShowError);

			ProgressTask pt = new ProgressTask(App.Kp2a, this, runnable);
			pt.Run();

		}	
		public void ToggleVisibility()
		{
			_showPassword = !_showPassword;
			SetPasswordStyle();
			UpdateTogglePasswordMenu();
		}


		public bool GotoUrl()
		{
			string url = _stringViews[PwDefs.UrlField].Text;
			if (url == null) return false;

			// Default http:// if no protocol specified
			if ((!url.Contains(":") || (url.StartsWith("www."))))
			{
				url = "http://" + url;
			}

			try
			{
				Util.GotoUrl(this, url);
			}
			catch (ActivityNotFoundException)
			{
				Toast.MakeText(this, Resource.String.no_url_handler, ToastLength.Long).Show();
			}
			return true;
		}

		public void AddEntryToIntent(Intent intent)
		{
			PluginHost.AddEntryToIntent(intent, App.Kp2a.GetDb().LastOpenedEntry);
		}

		public void CloseAfterTaskComplete()
		{
			//before closing, wait a little to get plugin updates
			int numPlugins = new PluginDatabase(this).GetPluginsWithAcceptedScope(Strings.ScopeCurrentEntry).Count();
			var timeToWait = TimeSpan.FromMilliseconds(500*numPlugins);
			SetProgressBarIndeterminateVisibility(true);
			_timer = new Timer(obj =>
				{
					RunOnUiThread(() =>
						{
							//task is completed -> return NullTask
							Intent resIntent = new Intent();
							new NullTask().ToIntent(resIntent);
							SetResult(KeePass.ExitCloseAfterTaskComplete, resIntent);
							//close activity:
							Finish();
						}
						);
				},
				null, timeToWait, TimeSpan.FromMilliseconds(-1));
		}

		public void ShowAttachedImage(string key)
		{
			ProtectedBinary pb = Entry.Binaries.Get(key);
			System.Diagnostics.Debug.Assert(pb != null);
			if (pb == null)
				throw new ArgumentException();
			byte[] pbData = pb.ReadData();		

			Intent imageViewerIntent = new Intent(this, typeof(ImageViewActivity));
			imageViewerIntent.PutExtra("EntryId", Entry.Uuid.ToHexString());
			imageViewerIntent.PutExtra("EntryKey", key);
			StartActivity(imageViewerIntent);
		}
	}
}