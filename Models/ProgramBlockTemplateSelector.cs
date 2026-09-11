using System.Windows;
using System.Windows.Controls;

namespace DUC_GUI.Models
{
    /// <summary>
    /// DataTemplateSelector chọn đúng DataTemplate cho từng loại ProgramBlock
    /// trong ListBox bên cột phải (PROGRAMMING).
    /// </summary>
    public class ProgramBlockTemplateSelector : DataTemplateSelector
    {
        public DataTemplate? MoveTemplate     { get; set; }
        public DataTemplate? DelayTemplate    { get; set; }
        public DataTemplate? GpioTemplate     { get; set; }
        public DataTemplate? IfBranchTemplate { get; set; }
        public DataTemplate? ElseBranchTemplate { get; set; }
        public DataTemplate? EndIfTemplate    { get; set; }

        public override DataTemplate? SelectTemplate(object item, DependencyObject container)
        {
            return item switch
            {
                MoveBlock       => MoveTemplate,
                DelayBlock      => DelayTemplate,
                GpioBlock       => GpioTemplate,
                IfBranchBlock   => IfBranchTemplate,
                ElseBranchBlock => ElseBranchTemplate,
                EndIfBlock      => EndIfTemplate,
                _               => base.SelectTemplate(item, container)
            };
        }
    }
}
