using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Runtime.Serialization;

namespace WpfDemo
{
    [DataContract]
    public sealed class WorkItem : INotifyPropertyChanged
    {
        private string _status;
        private string _note;

        [DataMember(Name = "id")]
        public int Id { get; set; }

        [DataMember(Name = "title")]
        public string Title { get; set; }

        [DataMember(Name = "category")]
        public string Category { get; set; }

        [DataMember(Name = "status")]
        public string Status
        {
            get { return _status; }
            set
            {
                if (_status == value) return;
                _status = value;
                OnPropertyChanged();
            }
        }

        [DataMember(Name = "note")]
        public string Note
        {
            get { return _note; }
            set
            {
                if (_note == value) return;
                _note = value;
                OnPropertyChanged();
            }
        }

        public event PropertyChangedEventHandler PropertyChanged;

        private void OnPropertyChanged([CallerMemberName] string name = null)
        {
            var handler = PropertyChanged;
            if (handler != null) handler(this, new PropertyChangedEventArgs(name));
        }
    }
}
