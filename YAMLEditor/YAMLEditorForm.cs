﻿using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Data;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Windows.Forms;
using YamlDotNet.RepresentationModel;
using YAMLEditor.Logging;
using YAMLEditor.Patterns;
using Microsoft.VisualBasic;
using WebSocketSharp;
using Newtonsoft.Json;
using YAMLEditor.Properties;
using Renci.SshNet;

namespace YAMLEditor
{
    public partial class YAMLEditorForm : Form
    {
        //---------------------------- Variables ----------------------------

        /// <summary>
        /// Command Manager (handles undos and redos)
        /// </summary>
        public static CommandManager Manager = new CommandManager();


        /// <summary>
        /// // Logger (used to print to the console window)
        /// </summary>
        private static ILogger mLogger = Logging.Logger.Instance;


        // IO variables
        public static string filename;
        public static string openedfilename;
        public static string workingdir;


        // Other variables
        public static IComponent currentParent;
        public static TreeNode parentNode = new TreeNode();
        public static bool componentExists { get; set; } = false;


        //---------------------------- Data structures ----------------------------

        /// <summary>
        /// Holds all the components that make up the opened file's yaml structure
        /// </summary>
        public static IComponent composite { get; set; }

        /// <summary>
        /// Holds all the tree nodes that make up the Tree View
        /// </summary>
        public static TreeNode FileTreeRoot { get; set; }


        //---------------------------- Edited Components ----------------------------

        /// <summary>
        /// Holds the components that suffered changes
        /// {K, V}, K -> {oldvalue, parents at the time}, V -> component that changed
        /// </summary>
        public static Dictionary<Dictionary<string, List<IComponent>>, IComponent> changedComponents { get; set; }

        /// <summary>
        /// Holds the components that were added
        /// </summary>
        public static List<IComponent> addedComponents { get; set; }

        /// <summary>
        /// Holds the components that were removed
        /// {K, V}, K -> component that got removed, V -> parents at the time
        /// </summary>
        public static Dictionary<IComponent, List<IComponent>> removedComponents { get; set; }


        //---------------------------------------------------------------------------

        public YAMLEditorForm()
        {
            InitializeComponent();
            mainTabControl.SelectedIndexChanged += new EventHandler(LoadHelpPageEvent);

            mLogger.Recorder = new TextBoxRecorder(mainTextBox);
            composite = new Component("root", "root", null);
            currentParent = composite; // current parent is the root
            changedComponents = new Dictionary<Dictionary<string, List<IComponent>>, IComponent>();
            addedComponents = new List<IComponent>();
            removedComponents = new Dictionary<IComponent, List<IComponent>>();

            workingdir = Environment.CurrentDirectory; // keep the directory we are working in

            Application.ApplicationExit += new EventHandler(this.OnApplicationExit);
			this.CenterToScreen();
        }

		/// <summary>
		/// When exiting the program, we delete all temporary files that we created either by SFTP or by Github repositories
		/// </summary>
		/// <param name="sender"></param>
		/// <param name="e"></param>
        private void OnApplicationExit(object sender, EventArgs e)
        {
            Directory.SetCurrentDirectory(workingdir);

            if (Directory.Exists("./RemoteFiles/"))
            {
                DirectoryInfo d = new DirectoryInfo("./RemoteFiles/");

                // Delete all files and subdirectories
                foreach (FileInfo file in d.GetFiles())
                {
                    file.Delete();
                }
                foreach (DirectoryInfo dir in d.GetDirectories())
                {
                    dir.Delete(true);
                }

                // Delete the directory
                d.Delete();
            }

            if (Directory.Exists(workingdir + "/GitRepo/"))
            {
                DirectoryInfo d = new DirectoryInfo(workingdir + "/GitRepo/");

                foreach (FileInfo file in d.GetFiles())
                {
                    file.Delete();
                }
                foreach (DirectoryInfo dir in d.GetDirectories())
                {
                    setAttributesNormal(dir);
                    dir.Delete(true);
                }

                d.Delete();
            }
        }


        #region Button Actions
		
		/// <summary>
		/// Adds a component to the selected node
		/// </summary>
		/// <param name="sender"></param>
		/// <param name="e"></param>
        private void OnNewComponent(object sender, EventArgs e)
        {
            IComponent component;

			// If we don't have a selected node in the tree or we have the root node selected, the parent of the new node will be the root node
            if (mainTreeView.SelectedNode == null || mainTreeView.SelectedNode.Tag == null)
                component = composite;
			// Else the selected node will be the parent of the new node
			else
                component = mainTreeView.SelectedNode.Tag as Component;

            if (component.Name != "root")
            {
                List<IComponent> allchildren = new List<IComponent>();
                allchildren = GetAllChildren(allchildren, component);

				// We validate if the selected node can have a new component added as a child
                if ((allchildren.Count == 1 && allchildren.First().Name != "") || allchildren.Count == 0)
                {
                    if(component.Name == "")
                        MessageBox.Show("Can't add a new component to this node. Try adding to " + component.getParent().Name + ".");
                    else
                        MessageBox.Show("Can't add a new component to this node.");
                    return;
                }
            }

            NewComponent nc = new NewComponent(component);

            nc.ShowDialog();
        }

		/// <summary>
		/// Exits the application
		/// </summary>
		/// <param name="sender"></param>
		/// <param name="e"></param>
        private void OnExit(object sender, EventArgs e)
        {
            Application.Exit();
        }

		/// <summary>
		/// Allows the user to choose a yaml file to edit
		/// </summary>
		/// <param name="sender"></param>
		/// <param name="e"></param>
        private void OnOpen(object sender, EventArgs e)
        {
            Directory.SetCurrentDirectory(workingdir);
            var dialog = new OpenFileDialog()
            { Filter = @"Yaml files (*.yaml)|*.yaml|All files (*.*)|*.*", DefaultExt = "yaml" };

			// When opening a yaml file
			if (dialog.ShowDialog() == DialogResult.OK)
            {
                // clear the edited components lists
                changedComponents = new Dictionary<Dictionary<string, List<IComponent>>, IComponent>();
                addedComponents = new List<IComponent>();

                // clear the tree
                if(FileTreeRoot != null)
                    FileTreeRoot.Nodes.Clear();

                // create new composite
                composite = new Component("root", "root", null);

                // update the parent
                currentParent = composite;

                mLogger.WriteLine(DateTime.Now.ToString("HH:mm:ss") + " - Opened " + $"Filename: {dialog.FileName}");
                System.Diagnostics.Trace.WriteLine($"Filename: {dialog.FileName}");
                Directory.SetCurrentDirectory(Path.GetDirectoryName(dialog.FileName) ?? "");

                mainTreeView.Nodes.Clear();
                FileTreeRoot = mainTreeView.Nodes.Add(Path.GetFileName(dialog.FileName));
                FileTreeRoot.ImageIndex = FileTreeRoot.SelectedImageIndex = 3;

                // get filename from the filepath
                openedfilename = dialog.FileName;
                var splits = openedfilename.Split('\\');
                openedfilename = splits[splits.Length - 1];
                filename = openedfilename;

                // populate data structures from file
                LoadFile(FileTreeRoot, filename);
                FileTreeRoot.Expand();

                // After opening a file we enable these buttons
                newComponentButton.Enabled = true;
                newComponentMenuItem.Enabled = true;
                removeToolStripButton.Enabled = true;
                removeToolStripMenuItem.Enabled = true;
                saveToolStripButton.Enabled = true;
                saveToolStripMenuItem.Enabled = true;
                uploadtourl.Enabled = true;
                push_toolStrip.Enabled = true;
            }
        }

		/// <summary>
		/// Saves all changes made
		/// </summary>
		/// <param name="sender"></param>
		/// <param name="e"></param>
        private void OnSaveButton(object sender, EventArgs e)
        {
            Save();
        }

		/// <summary>
		/// Undoes (if possible) last change
		/// </summary>
		/// <param name="sender"></param>
		/// <param name="e"></param>
        private void OnUndo(object sender, EventArgs e)
        {
            Manager.Undo();
        }

		/// <summary>
		/// Redoes (if possible) last undo
		/// </summary>
		/// <param name="sender"></param>
		/// <param name="e"></param>
        private void OnRedo(object sender, EventArgs e)
        {
            Manager.Redo();
        }

		/// <summary>
		/// Removes the selected node
		/// </summary>
		/// <param name="sender"></param>
		/// <param name="e"></param>
        private void OnRemoveComponent(object sender, EventArgs e)
        {
			// If there is no node selected
            if (mainTreeView.SelectedNode == null)
            {
                MessageBox.Show("There is no node currently selected", "Error");
                return;
            }

            var component = mainTreeView.SelectedNode.Tag as Component;

            // Display confirmation popup
            DialogResult result = MessageBox.Show("Do you really want to remove " + component.Name + "?", "Warning",
            MessageBoxButtons.YesNo, MessageBoxIcon.Warning);

			// If the user cancelled the component removal
            if (result == DialogResult.No)
            {
                MessageBox.Show("Removal cancelled");
                mLogger.WriteLine(DateTime.Now.ToString("HH:mm:ss") + " - " + "Removal cancelled");
                return;
            }

            // Get the component
            var treenode = mainTreeView.SelectedNode;
            var node = (Component)treenode.Tag;

            List<IComponent> nodeparents = new List<IComponent>();
            nodeparents = GetParents(nodeparents, node);
            nodeparents.Remove(nodeparents.Last());

            // Remove component and tree node from their parent's children (remove from data structure)
            treenode.Parent.Nodes.Remove(treenode);
            node.getParent().getChildren().Remove(node);

            // Add to the list of edited components
            removedComponents.Add(node, nodeparents);

            mLogger.WriteLine(DateTime.Now.ToString("HH:mm:ss") + " - Removed " + component.Name);
        }

		/// <summary>
		/// Shows the developer team
		/// </summary>
		/// <param name="sender"></param>
		/// <param name="e"></param>
        private void OnAboutButton(object sender, EventArgs e)
        {
            MessageBox.Show("Developed by: " +
                "Diogo Cruz, " +
                "Diogo Nóbrega, " +
                "Francisco Teixeira, " +
                "Marco Lima", "About");
        }

		/// <summary>
		/// Restarts HomeAssistant on specified home assistant address
		/// </summary>
		/// <param name="sender"></param>
		/// <param name="e"></param>
		private void OnRestartHomeassistant(object sender, EventArgs e)
        {
            // Gets the stored details from the options menu
            string user_ha_address = (string)Settings.Default["ha_address"];
            string user_access_token = (string)Settings.Default["access_token"];

            // If they are not filled in then we can't proceed
            if (user_ha_address == "" || user_access_token == "")
            {
                MessageBox.Show("You need to specify your details. Check settings.", "Error");
                mLogger.WriteLine(DateTime.Now.ToString("HH:mm:ss") + " - You need to specify your details. Check settings.");
                return;
            }

            try
            {
				// Establishes the web socket connection with the server running on the user_ha_address with the user_access_token
				using (var ws = new WebSocket("ws://" + user_ha_address + "/api/websocket"))
                {
                    ws.Connect();

                    // Auth message
                    Dictionary<string, string> auth = new Dictionary<string, string>() { { "type", "auth" }, { "access_token", user_access_token } };
                    string json = JsonConvert.SerializeObject(auth);

                    // Authenticate
                    ws.Send(json);

					// Sends an api restart service request
                    var service = new Dictionary<object, object>() { { "type", "call_service" }, { "domain", "homeassistant" }, { "service", "restart" }, { "service_data", new Dictionary<string, string>() { } }, { "id", "14" } };

                    json = JsonConvert.SerializeObject(service);

                    // Send restart request
                    ws.Send(json);

                    mLogger.WriteLine(DateTime.Now.ToString("HH:mm:ss") + " - Restarting HomeAssistant on address " + user_ha_address);
                }
            }
            catch (Exception exc)
            {
                MessageBox.Show("Couldn't restart HomeAssistant. Check settings.", "Error");
                mLogger.WriteLine(DateTime.Now.ToString("HH:mm:ss") + " - Couldn't restart HomeAssistant. Check settings.");
            }
        }

		/// <summary>
		/// Opens the settings menu
		/// </summary>
		/// <param name="sender"></param>
		/// <param name="e"></param>
        private void OnOptionsToolStripMenuItem_Click(object sender, EventArgs e)
        {
            OptionsWindow options = new OptionsWindow();
            options.ShowDialog();
        }

		/// <summary>
		/// Shows the Help page of HomeAssistant
		/// </summary>
		/// <param name="sender"></param>
		/// <param name="e"></param>
        private void OnAfterSelect(object sender, TreeViewEventArgs e)
        {
            mainPropertyGrid.SelectedObject = e.Node.Tag;
            LoadHelpPage();
        }

		/// <summary>
		/// Downloads remote files through SFTP
		/// </summary>
		/// <param name="sender"></param>
		/// <param name="e"></param>
        private void OnOpenFromURL(object sender, EventArgs e)
        {
            OpenFromURL();
        }

		/// <summary>
		/// Uploads the remote files through SFTP
		/// </summary>
		/// <param name="sender"></param>
		/// <param name="e"></param>
        private void OnUploadToURL(object sender, EventArgs e)
        {
            UploadToURL();
        }

		/// <summary>
		/// Downloads remotes files through SFTP
		/// </summary>
		/// <param name="sender"></param>
		/// <param name="e"></param>
		private void OnToolStripOpenFromURL(object sender, EventArgs e)
        {
            OpenFromURL();
        }

		/// <summary>
		/// Uploads the remote files through SFTP
		/// </summary>
		/// <param name="sender"></param>
		/// <param name="e"></param>
		private void OnToolStripUploadToRemote(object sender, EventArgs e)
        {
            UploadToURL();
        }

		/// <summary>
		/// Pulls the files from Github repository
		/// </summary>
		/// <param name="sender"></param>
		/// <param name="e"></param>
        private void OnPullFromRemote(object sender, EventArgs e)
        {
            PullFromRemote();
        }

		/// <summary>
		/// Pushes the files to Github repository
		/// </summary>
		/// <param name="sender"></param>
		/// <param name="e"></param>
        private void OnPushToRemote(object sender, EventArgs e)
        {
            PushToRemote();
        }

		/// <summary>
		/// Pulls the files from Github repository
		/// </summary>
		/// <param name="sender"></param>
		/// <param name="e"></param>
		private void OnToolStripPullFromRemote(object sender, EventArgs e)
        {
            PullFromRemote();
        }

		/// <summary>
		/// Pushes the files to Github repository
		/// </summary>
		/// <param name="sender"></param>
		/// <param name="e"></param>
		private void OnToolStripPushToRemote(object sender, EventArgs e)
        {
            PushToRemote();
        }

		/// <summary>
		/// Shows the Help page of the selected component
		/// </summary>
		/// <param name="sender"></param>
		/// <param name="e"></param>
		public void LoadHelpPageEvent(object sender, EventArgs e)
        {
            try
            {
                LoadHelpPage();
            }
            catch (Exception exception)
            {
                mLogger.WriteLine(DateTime.Now.ToString("HH:mm:ss") + " - " + exception.StackTrace);
            }
        }
        #endregion

		/// <summary>
		/// Downloads the remote files through SFTP
		/// </summary>
        public void OpenFromURL()
        {
			// Caches the necessary options inputs from the settings menu
            string rh_address = (string)Settings.Default["rh_address"];
            string username = (string)Settings.Default["username"];
            string password = (string)Settings.Default["password"];
            string remote_dir = (string)Settings.Default["remote_directory"];

			// If any of these fields is empty, we can't download the files
            if (rh_address == "" || username == "" || remote_dir == "") // no password -> ""
            {
                MessageBox.Show("Please check your remote host file editing settings.", "Error");
                mLogger.WriteLine(DateTime.Now.ToString("HH:mm:ss") + " - Please check your remote host file editing settings before trying to open from URL.");
                return;
            }
            else
            {
				// Requires confirmation input
                DialogResult result = MessageBox.Show("Starting download of files from remote host on " + rh_address, "Open from URL",
                MessageBoxButtons.OKCancel, MessageBoxIcon.Information);
                mLogger.WriteLine(DateTime.Now.ToString("HH:mm:ss") + " - Starting download of files from remote host on " + rh_address);

                if (result == DialogResult.Cancel)
                    return;

                Directory.SetCurrentDirectory(workingdir);

				// If this directory already exists, then we delete all its content to avoid any conficts
                if (Directory.Exists("./RemoteFiles/"))
                {
                    DirectoryInfo d = new DirectoryInfo("./RemoteFiles/");

                    foreach (FileInfo file in d.GetFiles())
                    {
                        file.Delete();
                    }
                    foreach (DirectoryInfo dir in d.GetDirectories())
                    {
                        dir.Delete(true);
                    }
                }

				// Making sure the file path is correct
                if (!remote_dir.StartsWith("/"))
                    remote_dir = "/" + remote_dir;
                if (!remote_dir.EndsWith("/"))
                    remote_dir = remote_dir + "/";

				// Tries to download the files through SFTP
                try
                {
                    SFTPManager.DownloadRemoteFiles(rh_address, username, password, remote_dir, "./RemoteFiles/", "yaml");
                }
                catch (Exception exc)
                {
                    MessageBox.Show("Couldn't download files from remote host.", "Error");
                    mLogger.WriteLine(DateTime.Now.ToString("HH:mm:ss") + " - Couldn't download files from remote host.");
                    return;
                }

				// If the download is sucessful, the user then needs to open a yaml file from this directory 
                result = MessageBox.Show("Download complete. Open a file...", "Success",
                MessageBoxButtons.OK, MessageBoxIcon.Information);
                mLogger.WriteLine(DateTime.Now.ToString("HH:mm:ss") + " - Download complete.");

                var dialog = new OpenFileDialog()
                { Filter = @"Yaml files (*.yaml)|*.yaml|All files (*.*)|*.*", DefaultExt = "yaml" };

                dialog.InitialDirectory = workingdir + "\\RemoteFiles\\";

				// Opens the chosen yaml file to edit
                if (dialog.ShowDialog() == DialogResult.OK)
                {
                    changedComponents = new Dictionary<Dictionary<string, List<IComponent>>, IComponent>();
                    addedComponents = new List<IComponent>();
                    if (FileTreeRoot != null)
                        FileTreeRoot.Nodes.Clear();
                    composite = new Component("root", "root", null);
                    currentParent = composite;

                    System.Diagnostics.Trace.WriteLine($"Filename: {dialog.FileName}");

                    Directory.SetCurrentDirectory(Path.GetDirectoryName(dialog.FileName) ?? "");

                    mainTreeView.Nodes.Clear();
                    FileTreeRoot = mainTreeView.Nodes.Add(Path.GetFileName(dialog.FileName));
                    FileTreeRoot.ImageIndex = FileTreeRoot.SelectedImageIndex = 3;

                    openedfilename = dialog.FileName;
                    var splits = openedfilename.Split('\\');
                    openedfilename = splits[splits.Length - 1];
                    filename = openedfilename;

                    mLogger.WriteLine(DateTime.Now.ToString("HH:mm:ss") + " - Opened " + $"Filename: " + filename + " from remote host on " + rh_address);

                    LoadFile(FileTreeRoot, filename);
                    FileTreeRoot.Expand();

                    // After opening a file we enable these buttons
                    newComponentButton.Enabled = true;
                    newComponentMenuItem.Enabled = true;
                    removeToolStripButton.Enabled = true;
                    removeToolStripMenuItem.Enabled = true;
                    saveToolStripButton.Enabled = true;
                    saveToolStripMenuItem.Enabled = true;
                    uploadtourl.Enabled = true;
                }
            }
        }

		/// <summary>
		/// Uploads the remote files through SFTP
		/// </summary>
        public void UploadToURL()
        {
			// Caches the necessary options inputs from the settings menu
			string rh_address = (string)Settings.Default["rh_address"];
            string username = (string)Settings.Default["username"];
            string password = (string)Settings.Default["password"];
            string remote_dir = (string)Settings.Default["remote_directory"];

			// If any of these fields is empty, we can't download the files
			if (rh_address == "" || username == "" || remote_dir == "") // no password -> ""
            {
                MessageBox.Show("Please check your remote host file editing settings.", "Error");
                mLogger.WriteLine(DateTime.Now.ToString("HH:mm:ss") + " - Please check your remote host file editing settings before trying to upload to remote host.");
                return;
            }
            else
            {
				// Requires confirmation input
				DialogResult result = MessageBox.Show("Starting upload of files to remote host on " + rh_address, "Uploading to Remote",
                MessageBoxButtons.OKCancel, MessageBoxIcon.Information);
                mLogger.WriteLine(DateTime.Now.ToString("HH:mm:ss") + " - Starting upload of files to remote host on " + rh_address);

                if (result == DialogResult.Cancel)
                    return;

                Directory.SetCurrentDirectory(workingdir);

				// Tries to upload the files through SFTP
                try
                {
                    SFTPManager.UploadRemoteFiles(rh_address, username, password, "./RemoteFiles/", remote_dir, "yaml");
                }
                catch (Exception exc)
                {
                    MessageBox.Show("Couldn't upload files to remote host.", "Error");
                    mLogger.WriteLine(DateTime.Now.ToString("HH:mm:ss") + " - Couldn't upload files to remote host.");
                    return;
                }

                result = MessageBox.Show("Upload complete.", "Success",
                MessageBoxButtons.OK, MessageBoxIcon.Information);
                mLogger.WriteLine(DateTime.Now.ToString("HH:mm:ss") + " - Upload complete.");
            }
        }

		/// <summary>
		/// Pulls the remote files from the designated Github repository
		/// </summary>
        private void PullFromRemote()
        {
			// Cashes the necessary options inputs from the settings menu
			string gitrepo_link = (string)Settings.Default["gitrepo_link"];
            string gitrepo_email = (string)Settings.Default["gitrepo_email"];
            string gitrepo_password = (string)Settings.Default["gitrepo_password"];

			// If any of these fields is empty, we can't access the designated repository
			if (gitrepo_link == "" || gitrepo_email == "") // no password -> ""
            {
                MessageBox.Show("Please check your git settings.", "Error");
                mLogger.WriteLine(DateTime.Now.ToString("HH:mm:ss") + " - Please check your git settings before trying to pull from remote.");
                return;
            }
            else
            {
				// Requires confirmation input
				DialogResult result = MessageBox.Show("Starting pull of files from remote host on " + gitrepo_link, "Pull from Remote",
                MessageBoxButtons.OKCancel, MessageBoxIcon.Information);
                mLogger.WriteLine(DateTime.Now.ToString("HH:mm:ss") + " - Starting pull of files from remote host on " + gitrepo_link);

                if (result == DialogResult.Cancel)
                    return;

                Directory.SetCurrentDirectory(workingdir);

				// If this directory already exists, then we delete all its content to avoid any conficts
				if (Directory.Exists(workingdir + "/GitRepo/"))
                {
                    DirectoryInfo d = new DirectoryInfo(workingdir + "/GitRepo/");

                    foreach (FileInfo file in d.GetFiles())
                    {
                        file.Delete();
                    }
                    foreach (DirectoryInfo dir in d.GetDirectories())
                    {
                        setAttributesNormal(dir);
                        dir.Delete(true);
                    }
                }

				// Tries to pull the remote files from the Github repository
                try
                {
                    GitRepoManager.clone(gitrepo_email, gitrepo_password, gitrepo_link, workingdir + "/GitRepo/");
                }
                catch (Exception exc)
                {
                    MessageBox.Show("Couldn't pull files from remote.", "Error");
                    mLogger.WriteLine(DateTime.Now.ToString("HH:mm:ss") + " - Couldn't pull files from remote.");
                    return;
                }

				result = MessageBox.Show("Pull complete. Open a file...", "Success",
                MessageBoxButtons.OK, MessageBoxIcon.Information);
                mLogger.WriteLine(DateTime.Now.ToString("HH:mm:ss") + " - Pull complete.");

				// If the clonning of the files is sucessful, the user then needs to open a yaml file from this directory
				var dialog = new OpenFileDialog()
                { Filter = @"Yaml files (*.yaml)|*.yaml|All files (*.*)|*.*", DefaultExt = "yaml" };

                dialog.InitialDirectory = workingdir + "\\GitRepo\\";

				// Opens the chosen yaml file to edit
				if (dialog.ShowDialog() == DialogResult.OK)
                {
                    changedComponents = new Dictionary<Dictionary<string, List<IComponent>>, IComponent>();
                    addedComponents = new List<IComponent>();
                    if (FileTreeRoot != null)
                        FileTreeRoot.Nodes.Clear();
                    composite = new Component("root", "root", null);
                    currentParent = composite;

                    System.Diagnostics.Trace.WriteLine($"Filename: {dialog.FileName}");

                    Directory.SetCurrentDirectory(Path.GetDirectoryName(dialog.FileName) ?? "");

                    mainTreeView.Nodes.Clear();
                    FileTreeRoot = mainTreeView.Nodes.Add(Path.GetFileName(dialog.FileName));
                    FileTreeRoot.ImageIndex = FileTreeRoot.SelectedImageIndex = 3;

                    openedfilename = dialog.FileName;
                    var splits = openedfilename.Split('\\');
                    openedfilename = splits[splits.Length - 1];
                    filename = openedfilename;

                    mLogger.WriteLine(DateTime.Now.ToString("HH:mm:ss") + " - Opened " + $"Filename: " + filename + " from remote on " + gitrepo_link);

                    LoadFile(FileTreeRoot, filename);
                    FileTreeRoot.Expand();

                    // After opening a file we enable these buttons
                    newComponentButton.Enabled = true;
                    newComponentMenuItem.Enabled = true;
                    removeToolStripButton.Enabled = true;
                    removeToolStripMenuItem.Enabled = true;
                    saveToolStripButton.Enabled = true;
                    saveToolStripMenuItem.Enabled = true;
                    push_toolStrip.Enabled = true;
                }
            }
        }

		/// <summary>
		/// Commits and pushes the remote files to the designated Github repository
		/// </summary>
        private void PushToRemote()
        {
			// Cashes the necessary options inputs from the settings menu
			string gitrepo_link = (string)Settings.Default["gitrepo_link"];
            string gitrepo_email = (string)Settings.Default["gitrepo_email"];
            string gitrepo_password = (string)Settings.Default["gitrepo_password"];

			// If any of these fields is empty, we can't access the designated repository
			if (gitrepo_link == "" || gitrepo_email == "") // no password -> ""
            {
                MessageBox.Show("Please check your git settings.", "Error");
                mLogger.WriteLine(DateTime.Now.ToString("HH:mm:ss") + " - Please check your git settings before trying to push to remote.");
                return;
            }
            else
            {
				// Requires confirmation input
				DialogResult result = MessageBox.Show("Starting push of files to remote on " + gitrepo_link, "Pushing to Remote",
                MessageBoxButtons.OKCancel, MessageBoxIcon.Information);
                mLogger.WriteLine(DateTime.Now.ToString("HH:mm:ss") + " - Starting push of files to remote on " + gitrepo_link);

                if (result == DialogResult.Cancel)
                    return;

                Directory.SetCurrentDirectory(workingdir);

				// Tries to commit and push the remote files to the designated repository
                try
                {
                    List<string> files_paths = Directory.EnumerateFiles(workingdir + "/GitRepo/").ToList();

                    GitRepoManager.commit(gitrepo_email, workingdir + "/GitRepo/", files_paths);
                    GitRepoManager.push(gitrepo_email, gitrepo_password, workingdir + "/GitRepo/");
                }
                catch (Exception exc)
                {
                    MessageBox.Show("Couldn't push files to remote.", "Error");
                    mLogger.WriteLine(DateTime.Now.ToString("HH:mm:ss") + " - Couldn't push files to remote.");
                    return;
                }

                result = MessageBox.Show("Push complete.", "Success",
                MessageBoxButtons.OK, MessageBoxIcon.Information);
                mLogger.WriteLine(DateTime.Now.ToString("HH:mm:ss") + " - Push complete.");
            }
        }

		/// <summary>
		/// Shows the HomeAssistant help page for the selected node/component
		/// </summary>
        public void LoadHelpPage()
        {
			// If the help tab is selected
            if (mainTabControl.SelectedTab.Text == "Help")
            {
				// If there is no selected node we can't show the help page
                if (mainTreeView.SelectedNode == null || filename == null)
                    return;
				else if (mainTreeView.SelectedNode.Tag == null)
                    return;

                var node = (Component)mainTreeView.SelectedNode.Tag;

				// If the selected node is not the root node
                if (node.getParent().Name != "root")
                {
					// Get the selected node
                    List<IComponent> parents = new List<IComponent>();
                    node = GetParents(parents, node).ElementAt(parents.Count - 2) as Component;
                }

				// Load the HomeAssistant web page of the selected node
                webBrowser.Navigate("https://www.home-assistant.io/components/" + node.Name);
            }
        }

		/// <summary>
		/// Reads the designated yaml file and populates the data structures
		/// </summary>
		/// <param name="node"></param>
		/// <param name="filename"></param>
		private static void LoadFile(TreeNode node, string filename)
        {
            var yaml = new YamlStream();

			// Tries to read the file
            try
            {
                using(var stream = new StreamReader(filename))
                {
                    yaml.Load(stream);
                }
            }
            catch(Exception exception)
            {
                mLogger.WriteLine(DateTime.Now.ToString("HH:mm:ss") + " - " + exception.Message);
            }

            if(yaml.Documents.Count == 0) return;

			// Loads the children
            LoadChildren(node, yaml.Documents[0].RootNode as YamlMappingNode);
        }

		/// <summary>
		/// Parsers the yaml file and populates the data structures
		/// </summary>
		/// <param name="root"></param>
		/// <param name="mapping"></param>
        private static void LoadChildren(TreeNode root, YamlMappingNode mapping)
        {
            var children = mapping?.Children;
            if(children == null) return;

            foreach(var child in children)
            {
                var key = child.Key as YamlScalarNode;
                System.Diagnostics.Trace.Assert(key != null);

                if(child.Value is YamlScalarNode)
                {
                    var scalar = child.Value as YamlScalarNode;

                    IComponent comp = new Component(key.Value, filename, currentParent);
                    currentParent.add(comp);
                    currentParent = comp;

                    var node = root.Nodes.Add($"{key.Value}");
                    node.Tag = comp;
                    node.ImageIndex = node.SelectedImageIndex = GetImageIndex(scalar);

                    comp = new Component(scalar.Value, filename, currentParent);
                    currentParent.add(comp);

                    //if it's not another file, then add to tree
                    if (scalar.Tag != "!include")
                    {
                        node = root.Nodes[root.Nodes.Count - 1].Nodes.Add($"{scalar.Value}");
                        node.Tag = comp;
                        node.ImageIndex = node.SelectedImageIndex = GetImageIndex(scalar);
                    }

                    //if it's another file, then load that file
                    if (scalar.Tag == "!include")
                    {
                        var parentNode = node.Parent;
                        root.Nodes.Remove(node);
                        node = parentNode;
                        filename = scalar.Value;
                        LoadFile(node, scalar.Value);
                        filename = openedfilename;
                    }
                }
                else if(child.Value is YamlSequenceNode)
                {
                    IComponent comp = new Component(key.Value, filename, currentParent);
                    currentParent.add(comp);
                    currentParent = comp;

                    var node = root.Nodes.Add(key.Value);
                    node.Tag = comp;
                    node.ImageIndex = node.SelectedImageIndex = GetImageIndex(child.Value);

                    LoadChildren(node, child.Value as YamlSequenceNode);
                }
                else if(child.Value is YamlMappingNode)
                {
                    IComponent comp = new Component(key.Value, filename, currentParent);
                    currentParent.add(comp);
                    currentParent = comp;

                    var node = root.Nodes.Add(key.Value);
                    node.Tag = comp;
                    node.ImageIndex = node.SelectedImageIndex = GetImageIndex(child.Value);

                    LoadChildren(node, child.Value as YamlMappingNode);
                }

                if (currentParent.getParent() != null)
                    currentParent = currentParent.getParent();
            }
            
        }

		/// <summary>
		/// Gets the correct image according to the node type
		/// </summary>
		/// <param name="node"></param>
		/// <returns></returns>
        private static int GetImageIndex(YamlNode node)
        {
            switch(node.NodeType)
            {
                case YamlNodeType.Scalar:
                    if(node.Tag == "!secret") return 2;
                    if(node.Tag == "!include") return 1;
                    return 0;
                case YamlNodeType.Sequence: return 3;
                case YamlNodeType.Mapping:
                    if(node is YamlMappingNode mapping && mapping.Children.Any(pair => ((YamlScalarNode)pair.Key).Value == "platform")) return 5;
                    return 4;
            }
            return 0;
        }

		/// <summary>
		/// Parsers the yaml file and populates the data structures
		/// </summary>
		/// <param name="root"></param>
		/// <param name="sequence"></param>
		private static void LoadChildren(TreeNode root, YamlSequenceNode sequence)
        {
            foreach(var child in sequence.Children)
            {
                if(child is YamlSequenceNode)
                {
					// Create new component and add it to the composite
                    IComponent comp = new Component(root.Text, filename, currentParent);
                    currentParent.add(comp);
                    currentParent = comp;

					// Add new node to the tree
                    var node = root.Nodes.Add(root.Text);
					// Set the node tag as the respective component
					node.Tag = comp;
                    node.ImageIndex = node.SelectedImageIndex = GetImageIndex(child);

                    LoadChildren(node, child as YamlSequenceNode);
                }
                else if(child is YamlMappingNode)
                {
                    LoadChildren(root, child as YamlMappingNode);
                }
                else if(child is YamlScalarNode)
                {
                    var scalar = child as YamlScalarNode;

					// Create new component and add it to the composite
					IComponent comp = new Component(root.Text, filename, currentParent);
                    currentParent.add(comp);
                    currentParent = comp;

					// Add new node to the tree
					var node = root.Nodes.Add(scalar.Value);
					// Set the node tag as the respective component
					node.Tag = comp;
                    node.ImageIndex = node.SelectedImageIndex = GetImageIndex(child);
                }
            }

            if (currentParent.getParent() != null)
                currentParent = currentParent.getParent();
        }

        public static void UpdateTree(IComponent component, TreeNode root, string aValue)
        {
            if (root == null)
                root = FileTreeRoot;

            var parents1 = new List<IComponent>();
            var parents2 = new List<TreeNode>();

            parents1 = GetParents(parents1, component);
            parents2 = GetParents(parents2, root);

            if(parents1.Count > 1)
                parents1.Remove(parents1.Last());
            if (parents2.Count > 1)
                parents2.Remove(parents2.Last());

            if (parents1.Count == 0 && parents1.Count == parents2.Count)
            {
                if (component.Name.Equals(root.Name))
                {
                    //parents1.Remove(parents1.Last());
                    //YAMLEditorForm.changedComponents.Add(new Dictionary<string, List<IComponent>>() { { node.Name, parents1 } }, component);
                    //root.Name = aValue;
                    root.Text = aValue;
                    return;
                }
            }
            else if (parents1.Count == parents2.Count)
            {
                for (var i = 0; i < parents1.Count; i++)
                {
                    if (parents1[i].Name != parents2[i].Text && parents2[i].Tag != null)
                        return;
                }

                var treecomp = root.Tag as Component;

                /*if (component.Name.Equals(root.Name) || component.Name.Equals(treecomp.Name))
                {
                    //parents1.Remove(parents1.Last());
                    //YAMLEditorForm.changedComponents.Add(new Dictionary<string, List<IComponent>>() { { node.Name, parents1 } }, component);
                    //root.Name = aValue;
                    root.Text = aValue;
                    return;
                }*/

                if (component.Name.Equals(treecomp.Name))
                {
                    //parents1.Remove(parents1.Last());
                    //YAMLEditorForm.changedComponents.Add(new Dictionary<string, List<IComponent>>() { { node.Name, parents1 } }, component);
                    //root.Name = aValue;
                    root.Text = aValue;
                    return;
                }
            }


            /*if (root == null)
                root = FileTreeRoot;

            if (root.Tag == component)
            {
                root.Text = component.Name;
                return;
            }*/

            foreach (TreeNode node in root.Nodes)
            {
                UpdateTree(component, node, aValue);
            }
        }


        public static void FindTreeNode(IComponent component, TreeNode root)
        {

            if (root == null)
                root = FileTreeRoot;

            if (root.Tag == component || (root.Tag == null && component == composite))
            {
                parentNode = root;
                return;
            }

            foreach (TreeNode node in root.Nodes)
            {
                FindTreeNode(component, node);
            }
        }

        public static void UpdateComposite(IComponent node, IComponent component, string aValue)
        {
            if (node == null)
                node = composite;

            var parents1 = new List<IComponent>();
            var parents2 = new List<IComponent>();

            parents1 = GetParents(parents1, node);
            parents2 = GetParents(parents2, component);

            if(parents1.Count > 0)
                parents1.Remove(parents1.Last());
            if (parents2.Count > 0)
                parents2.Remove(parents2.Last());

            if (parents1.Count == 0 && parents1.Count == parents2.Count)
            {
                if(node.Name.Equals(component.Name))
                {
                    //parents1.Remove(parents1.Last());
                    //YAMLEditorForm.changedComponents.Add(new Dictionary<string, List<IComponent>>() { { node.Name, parents1 } }, component);
                    node.setName(aValue);
                    return;
                }
            }
            else if(parents1.Count == parents2.Count)
            {
                for(var i = 0; i < parents1.Count; i++)
                {
                    if (!parents1[i].Name.Equals(parents2[i].Name))
                        return;
                }

                if (node.Name.Equals(component.Name))
                {
                    //parents1.Remove(parents1.Last());
                    //YAMLEditorForm.changedComponents.Add(new Dictionary<string, List<IComponent>>() { { node.Name, parents1 } }, component);
                    node.setName(aValue);
                    return;
                }
            }

            foreach (IComponent n in node.getChildren())
            {
                UpdateComposite(n, component, aValue);
            }
        }

        public static void CheckIfComponentExists(IComponent node, IComponent component)
        {
            if (node == null)
                node = composite;

            if (node.Name == component.Name)
            {
                componentExists = true;
                return;
            }

            foreach (IComponent n in node.getChildren())
            {
                CheckIfComponentExists(n, component);
            }
        }

        public static Dictionary<IComponent, TreeNode> getComponentFromFile(string filename)
        {
            string content = File.ReadAllText(filename);

            if (content.Trim() != "" && !content.Trim().StartsWith("#"))
            {
                // Get composite and tree from the opened component file
                IComponent newcomponent = new Component("root", filename, null);
                TreeNode newtree = new TreeNode();

                currentParent = newcomponent;
                LoadFile(newtree, filename);
                currentParent = composite;

                return new Dictionary<IComponent, TreeNode> { { newcomponent, newtree } };
            }
            else
            {
                MessageBox.Show("The file opened is empty.", "Error");
                return null;
            }
        }

        public static void AddComponentToData(IComponent aComponent, TreeNode aTree, IComponent aParent)
        {
            aComponent.setParent(aParent);
            aParent.add(aComponent);
            addedComponents.Add(aComponent);
            //var parentNode = new TreeNode();
            FindTreeNode(aParent, null);
            parentNode.Nodes.Add(aTree);

            mLogger.WriteLine(DateTime.Now.ToString("HH:mm:ss") + " - Added " + aComponent.Name);
        }

        public static List<IComponent> GetParents(List<IComponent> parents, IComponent node)
        {
            if (node.getParent() == null)
            {
                return parents;
            }
            else
            {
                parents.Add(node.getParent());
                return GetParents(parents, node.getParent());
            }
        }

        public static List<TreeNode> GetParents(List<TreeNode> parents, TreeNode node)
        {
            if (node.Parent == null)
            {
                return parents;
            }
            else
            {
                parents.Add(node.Parent);
                return GetParents(parents, node.Parent);
            }
        }

        public static List<IComponent> GetAllChildren(List<IComponent> children, IComponent node)
        {
            if(node.getChildren().Count == 0)
            {
                return children;
            }
            else
            {
                foreach(IComponent child in node.getChildren())
                {
                    children.Add(child);
                    GetAllChildren(children, child);
                }
            }

            return children;
        }

        public void Save()
        {
            // For each component that was added we write it to the opened file
            foreach (IComponent comp in addedComponents)
            {
                List<string> lines = new List<string>();

                try
                {
                    lines = File.ReadAllLines(filename).ToList();
                }
                catch(IOException ioe)
                {
                    mLogger.WriteLine(DateTime.Now.ToString("HH:mm:ss") + " - Couldn't save added component " + comp.Name);
                    continue;
                }

                if (comp.getParent().Name == "root")
                {
                    lines.Add("");
                    lines.Add(comp.Name + ": !include " + comp.getFileName());
                }
                else
                {
                    List<IComponent> nodeparents = new List<IComponent>();
                    nodeparents = GetParents(nodeparents, comp);
                    nodeparents.Remove(nodeparents.Last());

                    string ident = "";

                    for(var i = 0; i < nodeparents.Count - 1; i++)
                    {
                        ident += "  ";
                    }

                    int ln = 0;

                    // if it's a level 0 component, the parent will be null
                    // and as such, the "parent's" line will 0 (as if the
                    // hypothetical parent was the whole file)
                    if (nodeparents.Count != 0)
                    {
                        var aux = false;
                        for (var j = nodeparents.Count - 1; j >= 0; j--)// IComponent parent in nodeparents)
                        {
                            // For each line of this file we look for the component
                            for (var i = ln; i < lines.Count; i++)
                            {
                                if (lines[i].Trim().StartsWith(nodeparents[j].Name) || lines[i].Trim().StartsWith("- " + nodeparents[j].Name))//lines[i].Contains(nodeparents[j].Name))
                                {
                                    aux = true;
                                    ln = i;
                                    nodeparents.RemoveAt(j);
                                    break;
                                }
                            }

                            if (aux == false)
                                break; // didn't find the component - should never happen
                        }

                        if (aux == false)
                            continue; // didn't find the component - should never happen
                    }


                    List<IComponent> siblings = new List<IComponent>();
                    siblings = GetAllChildren(siblings, comp.getParent());

                    if (siblings.Count == 1 && siblings.First().Name == "")
                    {
                        comp.getParent().getChildren().RemoveAt(0);
                        lines[ln] = lines[ln] + " !include " + comp.getFileName();
                        break;
                    }
                    else
                    {
                        // after we've got the parent's exact line
                        for (var i = ln + 1; i < lines.Count; i++)
                        {
                            string line_ident = "";

                            foreach(Char c in lines[i])
                            {
                                if (c.Equals(' '))
                                    line_ident += " ";
                                else if (c.Equals('#'))
                                {
                                    line_ident = "#";
                                    break;
                                }
                                else
                                    break;
                            }

                            if (line_ident == "#")
                                continue;

                            if (lines[i].Trim() == "")
                            {
                                lines.Insert(i, ident + "  " + comp.Name + ": !include " + comp.getFileName());
                                break;
                            }
                            else if (ident == line_ident && !lines[i].Trim().StartsWith("#"))
                            {
                                lines.Insert(i - 1, ident + "  " + comp.Name + ": !include " + comp.getFileName());
                                break;
                            }
                            else if(i == lines.Count)
                            {
                                lines.Add(ident + "  " + comp.Name + ": !include " + comp.getFileName());
                                break;
                            }
                        }
                    }
                }

                try
                {
                    File.WriteAllLines(filename, lines);
                }
                catch(IOException ioe)
                {
                    mLogger.WriteLine(DateTime.Now.ToString("HH:mm:ss") + " - Couldn't save added components");
                }
            }

            // For each component that suffered changes we look for it in the files (opened and !included)
            foreach (KeyValuePair< Dictionary<string, List<IComponent>>, IComponent > comp in changedComponents)
            {
                // changed component
                var node = comp.Value;

                // its parents
                var nodeparents = comp.Key.Values.First();

                // its old value
                var oldvalue = comp.Key.Keys.First();

                string newvalue = node.Name;

                List<string> lines = new List<string>();

                try
                {
                    lines = File.ReadAllLines(node.getFileName()).ToList();
                }
                catch (IOException e)
                {
                    mLogger.WriteLine(DateTime.Now.ToString("HH:mm:ss") + " - Couldn't save changed component " + node.Name);
                    continue;
                }

                int ln = 0;

                // if it's a level 0 component, the parent will be null
                // and as such, the "parent's" line will 0 (as if the
                // hypothetical parent was the whole file)
                if (nodeparents.Count != 0)
                {
                    var aux = false;
                    for (var j = nodeparents.Count - 1; j >= 0; j--)// IComponent parent in nodeparents)
                    {
                        // For each line of this file we look for the component
                        for (var i = ln; i < lines.Count; i++)
                        {
                            if (lines[i].Trim().StartsWith(nodeparents[j].Name) || lines[i].Trim().StartsWith("- " + nodeparents[j].Name))//lines[i].Contains(nodeparents[j].Name))
                            {
                                aux = true;
                                ln = i;
                                nodeparents.RemoveAt(j);
                                break;
                            }
                        }

                        if (aux == false)
                            break; // didn't find the component - should never happen
                    }

                    if (aux == false)
                        continue; // didn't find the component - should never happen
                }

                // after we've got the parent's exact line
                for (var i = ln; i < lines.Count; i++)
                {
                    if (lines[i].Contains(oldvalue) && !lines[i].Trim().StartsWith("#"))
                    {
                        // "parent:" --> "parent: newvalue"
                        if (oldvalue == "" && newvalue != "")
                            lines[i] = lines[i] + " " + newvalue;

                        // "oldvalue:?" --> "newvalue:?"
                        else if (lines[i].Contains(oldvalue + ":") && oldvalue != "" && newvalue != "")
                        {
                            var splits = lines[i].Split(':');
                            if (splits.Length == 2)
                                lines[i] = splits[0].Replace(oldvalue, newvalue) + ":" + splits[1];
                            else
                                lines[i] = splits[0].Replace(oldvalue, newvalue) + ":";
                        }
                        else
                        {
                            // "parent: oldvalue" --> "parent:"
                            if (newvalue == "")
                                lines[i] = lines[i].Split(':')[0] + ":";

                            // "parent: oldvalue" --> "parent: newvalue"
                            else
                                lines[i] = lines[i].Split(':')[0] + ": " + newvalue;
                        }
                        break;
                    }
                }

                try
                {
                    File.WriteAllLines(node.getFileName(), lines);
                }
                catch(IOException ioe)
                {
                    mLogger.WriteLine(DateTime.Now.ToString("HH:mm:ss") + " - Couldn't save changed components");
                }
            }

            // For each component that was removed we remove it from the opened file
            foreach (KeyValuePair<IComponent, List<IComponent>> comp in removedComponents)
            {
                var nodeparents = comp.Value;
                var node = comp.Key;

                List<string> lines = new List<string>();

                try
                {
                    lines = File.ReadAllLines(node.getFileName()).ToList();
                }
                catch(IOException ioe)
                {
                    mLogger.WriteLine(DateTime.Now.ToString("HH:mm:ss") + " - Couldn't save removed component " + node.Name);
                    continue;
                }

                int ln = 0;

                var aux = false;

                if (nodeparents.Count > 1)
                {
                    for (var j = nodeparents.Count - 1; j >= 0; j--)// IComponent parent in nodeparents)
                    {
                        // For each line of this file we look for the component
                        for (var i = ln; i < lines.Count; i++)
                        {
                            if (lines[i].Trim().StartsWith(nodeparents[j].Name) || lines[i].Trim().StartsWith("- " + nodeparents[j].Name))//lines[i].Contains(nodeparents[j].Name))
                            {
                                aux = true;
                                ln = i;
                                nodeparents.RemoveAt(j);
                                break;
                            }
                        }

                        if (aux == false)
                            break; // didn't find the component - should never happen
                    }
                }

                // temos a linha do pai direto do no
                aux = false;
                for (var i = ln; i < lines.Count; i++)
                {
                    if (lines[i].Contains(node.Name) && !lines[i].Trim().StartsWith("#") && !aux)
                    {
                        lines.RemoveAt(i);
                        if(i > 0)
                            i--;
                        aux = true;
                    }

                    if (aux && node.getChildren().Count > 1)
                    {
                        foreach (IComponent child in node.getChildren())
                        {
                            if (lines[i].Contains(child.Name) && !lines[i].Trim().StartsWith("#"))
                            {
                                lines.RemoveAt(i);
                                if (i > 0)
                                    i--;
                            }
                        }
                    }

                    if (aux && lines[i].Trim() == "")
                        break;
                }

                try
                {
                    File.WriteAllLines(node.getFileName(), lines);
                }
                catch(IOException ioe)
                {
                    mLogger.WriteLine(DateTime.Now.ToString("HH:mm:ss") + " - Couldn't save removed components");
                }
            }

            changedComponents = new Dictionary<Dictionary<string, List<IComponent>>, IComponent>();
            addedComponents = new List<IComponent>();
            removedComponents = new Dictionary<IComponent, List<IComponent>>();
            FileTreeRoot.Nodes.Clear();
            composite = new Component("root", "root", null);
            currentParent = composite;
            LoadFile(FileTreeRoot, filename);

            mLogger.WriteLine(DateTime.Now.ToString("HH:mm:ss") + " - Saved");
        }

		/// <summary>
		/// Allows the logger to write a message in the console
		/// </summary>
		/// <param name="aMessage"></param>
        public static void WriteToTextBox(string aMessage)
        {
            mLogger.WriteLine(aMessage);
        }

        public static void AddComponent(IComponent aParent, string aFilename)
        {
            Dictionary<IComponent, TreeNode> fileRoot = YAMLEditorForm.getComponentFromFile(aFilename);
            var splits = aFilename.Split('\\');
            var name = splits[splits.Length - 1];

            if (fileRoot != null)
            {
                fileRoot.Keys.First().setFileName(name);

                //Check if this component already exists where we are trying to add
                YAMLEditorForm.componentExists = false;
                YAMLEditorForm.CheckIfComponentExists(aParent, fileRoot.Keys.First().getChild(0));

                if (YAMLEditorForm.componentExists)
                {
                    MessageBox.Show(fileRoot.Keys.First().getChild(0).Name + " already exists under " + aParent.Name + ".", "Error");
                }
                else
                {
                    MessageBox.Show(fileRoot.Keys.First().getChild(0).Name + " added successfully under " + aParent.Name + ".", "Success");
                    AddComponentToData(fileRoot.Keys.First().getChild(0), fileRoot.Values.First().Nodes[0], aParent);
                }
                YAMLEditorForm.componentExists = false;
            }
        }

        private void setAttributesNormal(DirectoryInfo dir)
        {
            foreach (var subDir in dir.GetDirectories())
            {
                setAttributesNormal(subDir);
                subDir.Attributes = FileAttributes.Normal;
            }
            foreach (var file in dir.GetFiles())
            {
                file.Attributes = FileAttributes.Normal;
            }
        }
    }
}