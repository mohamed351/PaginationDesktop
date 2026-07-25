namespace WindowsFormsApp1
{
    partial class Form1
    {
        /// <summary>
        /// Required designer variable.
        /// </summary>
        private System.ComponentModel.IContainer components = null;

        /// <summary>
        /// Clean up any resources being used.
        /// </summary>
        /// <param name="disposing">true if managed resources should be disposed; otherwise, false.</param>
        protected override void Dispose(bool disposing)
        {
            if (disposing && (components != null))
            {
                components.Dispose();
            }
            base.Dispose(disposing);
        }

        #region Windows Form Designer generated code

        /// <summary>
        /// Required method for Designer support - do not modify
        /// the contents of this method with the code editor.
        /// </summary>
        private void InitializeComponent()
        {
            this.gridPaging1 = new WindowsFormsApp1.GridPaging();
            this.SuspendLayout();
            // 
            // gridPaging1
            // 
            this.gridPaging1.DbContextTypeName = "WindowsFormsApp1.CompanyDatabaseEntities, WindowsFormsApp1, Version=1.0.0.0, Cult" +
    "ure=neutral, PublicKeyToken=null";
            this.gridPaging1.DbSetPropertyName = "Employees";
            this.gridPaging1.Dock = System.Windows.Forms.DockStyle.Fill;
            this.gridPaging1.Location = new System.Drawing.Point(0, 0);
            this.gridPaging1.Name = "gridPaging1";
            this.gridPaging1.Size = new System.Drawing.Size(882, 522);
            this.gridPaging1.TabIndex = 0;
            // 
            // Form1
            // 
            this.AutoScaleDimensions = new System.Drawing.SizeF(6F, 13F);
            this.AutoScaleMode = System.Windows.Forms.AutoScaleMode.Font;
            this.ClientSize = new System.Drawing.Size(882, 522);
            this.Controls.Add(this.gridPaging1);
            this.Name = "Form1";
            this.Text = "Form1";
            this.Load += new System.EventHandler(this.Form1_Load);
            this.ResumeLayout(false);

        }

        #endregion

        private GridPaging gridPaging1;
    }
}

